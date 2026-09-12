using UnityEngine;
using UnityEditor;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace UnityMCP.Editor
{
    // Unity HOSTS a tiny HTTP server; each MCP server process sends one request per tool call.
    //
    // The transport is stateless request/response (an authenticated POST with a JSON
    // `{ type, data, authToken }` body returns
    // the result as the JSON response, then the socket closes). It used to be a persistent WebSocket,
    // but keeping a long-lived socket alive across Unity's frequent domain reloads is what generated
    // almost all the connection-handling complexity - reconnect, ride-out, and a teardown that kept
    // stalling the reload on a blocked socket thread. With request/response there's no idle socket to
    // tear down: a reload is just "the next request retries". Unity still owns the one port, so any
    // number of Claude sessions share one Editor with no port race.
    //
    // This class owns the server lifecycle (bind/accept/teardown), request dispatch, and the log
    // buffer that get_logs reads. The HTTP wire handling (read request, write response) lives in
    // ClientConnection. HTTP is hand-rolled over TcpListener because Mono's HttpListener is unreliable.
    [InitializeOnLoad]
    public class UnityMCPConnection
    {
        // The Editor binds a DYNAMIC port. Multiple Editors can run at once, so a single fixed port
        // would collide; instead each picks an OS-assigned port and publishes it via InstanceRegistry
        // for MCP servers to discover. The chosen port is pinned in SessionState so it survives domain
        // reloads (the process lives on; only managed state resets) - that keeps a client's selected
        // instance reachable at the same address after a reload.
        private const string PortSessionKey = "UnityMCP.BoundPort";
        private static int boundPort;

        private static TcpListener listener;
        private static bool isListening;
        private static CancellationTokenSource serverCts;

        // Sockets with a request in flight, tracked from accept until the handler finishes. Teardown
        // closes them directly: on Mono, closing the socket is what unblocks a thread parked in a
        // socket read (the cancellation token doesn't reach it). Each request is short-lived, so this
        // set is usually empty - it only matters for the rare reload-mid-request.
        private static readonly HashSet<TcpClient> activeSockets = new HashSet<TcpClient>();

        private static string lastErrorMessage = "";
        private static readonly Queue<LogEntry> logBuffer = new Queue<LogEntry>();
        private static readonly int maxLogBufferSize = 1000;

        // Ring buffer of the most recent tool calls, surfaced live in the debug window so the
        // developer can watch what Claude is doing - each call carries a `comment` explaining why.
        // Capped; oldest entries drop off. Written from background request threads and read from the
        // main-thread OnGUI, so every access locks the queue (as logBuffer does).
        private static readonly Queue<CallRecord> recentCalls = new Queue<CallRecord>();
        private const int maxRecentCalls = 50;
        private static bool isLoggingEnabled = true;
        private static readonly EditorStateReporter editorStateReporter = new EditorStateReporter();

        // Diagnostics surfaced in the debug window.
        private static DateTime serverStartedUtc;
        private static string lastRequestType;
        private static DateTime lastRequestUtc;
        private static int totalRequests;

        // Public properties for the debug window. IsListening is the authoritative signal: the server
        // owns the port and is accepting requests. False means the bind failed (e.g. another process
        // holds the port) - see LastErrorMessage.
        public static bool IsListening => isListening;
        public static int BoundPort => boundPort;
        public static Uri ServerUri => new Uri($"http://127.0.0.1:{boundPort}/");
        public static string InstanceName => InstanceRegistry.Name;
        public static string InstanceId => InstanceRegistry.InstanceId;
        public static string LastErrorMessage => lastErrorMessage;
        public static DateTime ServerStartedUtc => serverStartedUtc;
        public static string LastRequestType => lastRequestType;
        public static DateTime LastRequestUtc => lastRequestUtc;
        public static int TotalRequestCount => totalRequests;
        public static int BufferedLogCount { get { lock (logBuffer) { return logBuffer.Count; } } }

        // A snapshot of one recorded call, handed to the debug window for display.
        public struct RecentCall
        {
            public string Type;
            public string Comment;
            public DateTime Utc;
        }

        // Newest-first snapshot of the recent-call ring buffer, for the debug window's panel.
        public static RecentCall[] GetRecentCalls()
        {
            lock (recentCalls)
            {
                var arr = new RecentCall[recentCalls.Count];
                // The queue iterates oldest-first; fill back-to-front so index 0 is the newest call.
                int i = recentCalls.Count - 1;
                foreach (var c in recentCalls)
                {
                    arr[i--] = new RecentCall { Type = c.Type, Comment = c.Comment, Utc = c.Utc };
                }
                return arr;
            }
        }

        public static bool IsLoggingEnabled
        {
            get => isLoggingEnabled;
            set
            {
                isLoggingEnabled = value;
                if (value)
                {
                    Application.logMessageReceived += HandleLogMessage;
                }
                else
                {
                    Application.logMessageReceived -= HandleLogMessage;
                }
            }
        }

        private class LogEntry
        {
            public string Message { get; set; }
            public string StackTrace { get; set; }
            public LogType Type { get; set; }
            public DateTime Timestamp { get; set; }
        }

        private class CallRecord
        {
            public string Type { get; set; }
            public string Comment { get; set; }
            public DateTime Utc { get; set; }
        }

        // Manual restart from the debug window (e.g. after a port-bind failure is resolved).
        public static void RetryConnection()
        {
            Debug.Log("[UnityMCP] Restarting MCP server...");
            StopServer();
            StartServer();
        }

        // Constructor called on editor startup (and again after every domain reload).
        static UnityMCPConnection()
        {
            Application.logMessageReceived += HandleLogMessage;
            isLoggingEnabled = true;

            Debug.Log("[UnityMCP] Plugin initialized");
            EditorApplication.delayCall += () =>
            {
                Debug.Log("[UnityMCP] Starting MCP server");
                StartServer();
            };
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            // Remove our discovery record when the Editor exits so MCP servers stop offering this
            // instance. A crash skips this; the MCP side self-heals by dropping records whose port
            // no longer answers.
            EditorApplication.quitting += OnEditorQuitting;
        }

        private static void OnEditorQuitting()
        {
            InstanceRegistry.Delete();
        }

        // A domain reload wipes all managed state (the listener, any in-flight socket, the
        // main-thread queue). With stateless request/response there's no persistent connection to
        // drain - just stop accepting and close any in-flight socket so its handler unwinds. The
        // [InitializeOnLoad] static constructor restarts the server in the new domain, and each
        // client's next request reconnects.
        private static void OnBeforeAssemblyReload()
        {
            StopServer();
        }

        private static void StartServer()
        {
            if (isListening) return;

            // Prefer the port pinned earlier this Editor session so the address stays stable across
            // domain reloads; 0 lets the OS assign one on first start. If a pinned port can't be
            // reclaimed (e.g. something else grabbed it while we were closed), fall back to a fresh one.
            int desired = SessionState.GetInt(PortSessionKey, 0);
            if (!TryStart(desired) && desired != 0)
            {
                SessionState.SetInt(PortSessionKey, 0);
                TryStart(0);
            }
        }

        private static bool TryStart(int desired)
        {
            try
            {
                serverCts = new CancellationTokenSource();
                // This endpoint can compile and execute arbitrary C#, so it must never be reachable
                // from the LAN. Use the explicit IPv4 loopback address and have the MCP side use the
                // same address, avoiding localhost/IPv6 resolver differences across platforms.
                listener = new TcpListener(IPAddress.Loopback, desired);
                // Allow reclaiming our own pinned port right after a domain reload (the previous
                // listener may linger briefly). Pinning is per-Editor-session, so no other Editor is
                // contending for this port.
                try { listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); } catch { }
                try { listener.ExclusiveAddressUse = false; } catch { }
                listener.Start();
                boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                SessionState.SetInt(PortSessionKey, boundPort);
                isListening = true;
                serverStartedUtc = DateTime.UtcNow;
                lastErrorMessage = "";
                // Publish (or refresh) this instance's discovery record now that the port is known.
                InstanceRegistry.Write(boundPort);
                Debug.Log($"[UnityMCP] HTTP server listening on http://127.0.0.1:{boundPort}/  " +
                          $"(instance '{InstanceRegistry.Name}' [{InstanceRegistry.InstanceId}])");
                _ = AcceptLoop(serverCts.Token);
                return true;
            }
            catch (Exception e)
            {
                isListening = false;
                lastErrorMessage = $"[UnityMCP] Failed to start server on port {(desired == 0 ? "(auto)" : desired.ToString())}: {e.Message}";
                Debug.LogError(lastErrorMessage);
                try { listener?.Stop(); } catch { }
                listener = null;
                // No reachable server - don't leave a record advertising a dead port.
                InstanceRegistry.Delete();
                return false;
            }
        }

        private static void StopServer()
        {
            isListening = false;
            try { serverCts?.Cancel(); } catch { }

            // Close any socket with a request in flight so its handler unwinds promptly. Closing is
            // the real unblock on Mono; the cancel token above is only a best-effort nudge.
            lock (activeSockets)
            {
                foreach (var s in activeSockets)
                {
                    try { s.Close(); } catch { }
                }
                activeSockets.Clear();
            }

            try { listener?.Stop(); } catch { }
            listener = null;
        }

        private static async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && isListening)
            {
                TcpClient tcp;
                try
                {
                    tcp = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    break; // listener stopped/disposed - exit quietly
                }

                if (token.IsCancellationRequested)
                {
                    try { tcp.Close(); } catch { }
                    break;
                }

                _ = HandleRequest(tcp, token);
            }
        }

        // Serves one request: read the HTTP body, dispatch it, write the JSON response, close.
        private static async Task HandleRequest(TcpClient tcp, CancellationToken token)
        {
            lock (activeSockets) activeSockets.Add(tcp);

            ClientConnection conn = null;
            try
            {
                if (token.IsCancellationRequested || !isListening)
                {
                    try { tcp.Close(); } catch { }
                    return;
                }

                conn = ClientConnection.Create(tcp);
                var request = await conn.ReadRequestAsync(token).ConfigureAwait(false);
                if (request == null) return; // closed / malformed

                var (status, statusText, json) =
                    await Dispatch(request.Value.method, request.Value.body, token).ConfigureAwait(false);
                // HEAD gets the same status and headers as GET but no body, per HTTP.
                bool includeBody = request.Value.method != "HEAD";
                await conn.WriteJsonResponseAsync(status, statusText, json, includeBody, token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // While tearing down (isListening == false) a read/write throwing on a closed socket
                // is expected - don't surface it as an error.
                if (isListening) Debug.LogError($"[UnityMCP] Request error: {e.Message}");
            }
            finally
            {
                lock (activeSockets) activeSockets.Remove(tcp);
                if (conn != null) conn.Close();
                else { try { tcp.Close(); } catch { } }
            }
        }

        // Routes an authenticated request body ({ type, data, authToken }) to the right handler and returns the HTTP status and
        // the JSON response payload. The payload IS the response body - there's no envelope or id,
        // since request/response is inherently correlated.
        private static async Task<(int status, string statusText, string json)> Dispatch(string method, string body, CancellationToken token)
        {
            // Non-POST methods aren't commands. GET/HEAD expose only a generic health response;
            // authenticated identity probes use POST. Anything else is rejected with 405.
            if (method != "POST")
            {
                if (method == "GET" || method == "HEAD")
                    return (200, "OK", JsonConvert.SerializeObject(new { status = "ok", server = "UnityMCP" }));
                return (405, "Method Not Allowed", JsonConvert.SerializeObject(new
                {
                    error = $"{method} not supported - POST a JSON {{ type, data }} command, or GET for a health check."
                }));
            }

            // A POST that carried no body (or omitted Content-Length) can't hold a command. Say so
            // plainly instead of falling through to a misleading "unknown request type".
            if (string.IsNullOrEmpty(body))
            {
                return (400, "Bad Request", JsonConvert.SerializeObject(new
                {
                    error = "Empty request body - POST a JSON { type, data } command with a Content-Length header."
                }));
            }

            string type = null;
            try
            {
                var msg = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                string authToken = msg != null && msg.ContainsKey("authToken") && msg["authToken"] != null
                    ? msg["authToken"].ToString()
                    : null;
                if (!string.Equals(authToken, InstanceRegistry.AuthToken, StringComparison.Ordinal))
                {
                    return (401, "Unauthorized", JsonConvert.SerializeObject(new
                    {
                        error = "Missing or invalid UnityMCP authentication token."
                    }));
                }

                type = msg != null && msg.ContainsKey("type") ? msg["type"]?.ToString() : null;
                string dataJson = msg != null && msg.ContainsKey("data") && msg["data"] != null
                    ? msg["data"].ToString()
                    : "{}";
                // The operator-facing "why this call" note, carried alongside type/data in the
                // envelope. Null for internal/legacy callers that don't send one.
                string comment = msg != null && msg.ContainsKey("comment") && msg["comment"] != null
                    ? msg["comment"].ToString()
                    : null;

                // Record every recognized command in the recent-calls ring buffer shown live in the
                // debug window. (get_command_page never reaches Unity, so it can't appear here.)
                if (type == "executeEditorCommand" || type == "getEditorState" ||
                    type == "takeScreenshot" || type == "getGameObjectDetails" ||
                    type == "getLogs" || type == "clearLogs")
                {
                    RecordCall(type, comment);
                }

                // Count only the "real work" calls in the debug-window telemetry, not log fetches.
                if (type == "executeEditorCommand" || type == "getEditorState" ||
                    type == "takeScreenshot" || type == "getGameObjectDetails")
                {
                    lastRequestType = type;
                    lastRequestUtc = DateTime.UtcNow;
                    Interlocked.Increment(ref totalRequests);
                }

                object payload;
                switch (type)
                {
                    case "executeEditorCommand":
                        try
                        {
                            payload = await EditorCommandExecutor.ExecuteAndGetResult(dataJson).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            // e.g. RunOnMainThread timed out (Editor unfocused). Return a failed result
                            // so the client reports it instead of waiting out its own timeout.
                            payload = new
                            {
                                result = (object)null,
                                logs = new List<string>(),
                                errors = new List<string> { e.Message },
                                warnings = new List<string>(),
                                executionSuccess = false,
                                errorDetails = new { message = e.Message, stackTrace = "", type = e.GetType().Name }
                            };
                        }
                        break;
                    case "getEditorState":
                        payload = await editorStateReporter.GetEditorStateData().ConfigureAwait(false);
                        break;
                    case "takeScreenshot":
                        payload = await new ScreenshotCapturer().GetScreenshotData(dataJson).ConfigureAwait(false);
                        break;
                    case "getGameObjectDetails":
                        payload = await new InspectorDataReporter().GetObjectDetailsData(dataJson).ConfigureAwait(false);
                        break;
                    case "getLogs":
                        payload = GetLogPayload();
                        break;
                    case "clearLogs":
                        payload = ClearLogPayload();
                        break;
                    case "identity":
                        // Lets an authenticated client confirm which instance answers this port.
                        payload = InstanceRegistry.Identity(boundPort);
                        break;
                    default:
                        return (400, "Bad Request",
                            JsonConvert.SerializeObject(new { error = $"Unknown request type: {type}" }));
                }

                return (200, "OK", JsonConvert.SerializeObject(payload));
            }
            catch (Exception e)
            {
                Debug.LogError($"[UnityMCP] Error handling request '{type}': {e.Message}");
                return (500, "Internal Server Error", JsonConvert.SerializeObject(new { error = e.Message }));
            }
        }

        // Snapshot of the buffered console logs, for get_logs (the client filters/slices).
        private static object GetLogPayload()
        {
            lock (logBuffer)
            {
                var list = new List<object>(logBuffer.Count);
                foreach (var e in logBuffer)
                {
                    list.Add(new
                    {
                        message = e.Message,
                        stackTrace = e.StackTrace,
                        logType = e.Type.ToString(),
                        timestamp = e.Timestamp.ToString("o")
                    });
                }
                return new { logs = list };
            }
        }

        // Empties the log buffer and reports how many entries were dropped, for clear_logs.
        private static object ClearLogPayload()
        {
            int cleared;
            lock (logBuffer)
            {
                cleared = logBuffer.Count;
                logBuffer.Clear();
            }
            return new { cleared };
        }

        // Append one call to the recent-calls ring buffer, evicting the oldest past the cap.
        private static void RecordCall(string type, string comment)
        {
            var record = new CallRecord { Type = type, Comment = comment, Utc = DateTime.UtcNow };
            lock (recentCalls)
            {
                recentCalls.Enqueue(record);
                while (recentCalls.Count > maxRecentCalls)
                {
                    recentCalls.Dequeue();
                }
            }
        }

        private static void HandleLogMessage(string message, string stackTrace, LogType type)
        {
            if (!isLoggingEnabled) return;

            var logEntry = new LogEntry
            {
                Message = message,
                StackTrace = stackTrace,
                Type = type,
                Timestamp = DateTime.UtcNow
            };

            lock (logBuffer)
            {
                logBuffer.Enqueue(logEntry);
                while (logBuffer.Count > maxLogBufferSize)
                {
                    logBuffer.Dequeue();
                }
            }
        }
    }
}
