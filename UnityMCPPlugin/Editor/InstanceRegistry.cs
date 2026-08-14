using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Debug = UnityEngine.Debug;

namespace UnityMCP.Editor
{
    // Publishes THIS Editor's presence so MCP servers can discover and address it by name.
    //
    // Multiple Unity Editors can run at once, each hosting its own HTTP server on its own dynamic
    // port (see UnityMCPConnection). There's no longer a fixed port to dial, so each Editor drops a
    // small JSON record - { instanceId, name, projectPath, port, authToken, pid, unityVersion } - into a shared
    // per-user directory. An MCP server lists that directory to enumerate running Editors and to
    // resolve a chosen instance name/id to its port. The record is (re)written whenever the server
    // binds (including after a domain reload, when the port is reclaimed from SessionState) and
    // deleted when the Editor quits. A crash leaves the file orphaned; the MCP side self-heals by
    // probing each port and dropping records that no longer answer.
    //
    // The directory path and the record's field names MUST stay in sync with the MCP side
    // (unity-mcp-server/src/communication/registry.ts). Both compute the directory the same way
    // per-OS, honoring a UNITYMCP_REGISTRY_DIR override.
    public static class InstanceRegistry
    {
        private const string AuthTokenSessionKey = "UnityMCP.AuthToken";
        private static string cachedInstanceId;
        private static string cachedName;
        private static string cachedProjectPath;
        private static string cachedAuthToken;

        // A short, stable hash of this project's root path. Stable across restarts and domain reloads,
        // so a client's selected instance keeps the same handle; also the record's filename.
        public static string InstanceId => cachedInstanceId ?? (cachedInstanceId = ComputeInstanceId(ProjectPath));

        // Human-facing handle: the project folder name. May collide across unrelated projects that
        // share a leaf name - InstanceId disambiguates.
        public static string Name => cachedName ?? (cachedName = SafeLeaf(ProjectPath));

        // Per-Editor-session capability used to authenticate every command. SessionState survives
        // domain reloads but is cleared when the Editor process exits, so existing MCP processes can
        // ride out recompiles without leaving a long-lived credential behind.
        public static string AuthToken
        {
            get
            {
                // Write() initializes this on Unity's main thread before AcceptLoop starts. Requests
                // run on worker threads and must use the cache: Unity's SessionState API throws when
                // called off the main thread.
                if (!string.IsNullOrEmpty(cachedAuthToken)) return cachedAuthToken;

                string token = SessionState.GetString(AuthTokenSessionKey, "");
                if (!string.IsNullOrEmpty(token))
                {
                    cachedAuthToken = token;
                    return cachedAuthToken;
                }

                var bytes = new byte[32];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
                token = Convert.ToBase64String(bytes);
                SessionState.SetString(AuthTokenSessionKey, token);
                cachedAuthToken = token;
                return cachedAuthToken;
            }
        }

        // The project root (parent of Assets), with forward slashes for a stable cross-tool id.
        public static string ProjectPath
        {
            get
            {
                if (cachedProjectPath == null)
                {
                    string root = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                    cachedProjectPath = root.Replace('\\', '/');
                }
                return cachedProjectPath;
            }
        }

        // The shared discovery directory. Kept identical to the MCP side; override with
        // UNITYMCP_REGISTRY_DIR for tests or non-standard setups.
        public static string RegistryDir()
        {
            string overrideDir = Environment.GetEnvironmentVariable("UNITYMCP_REGISTRY_DIR");
            if (!string.IsNullOrEmpty(overrideDir)) return overrideDir;

            switch (Application.platform)
            {
                case RuntimePlatform.OSXEditor:
                    return Path.Combine(Home(), "Library", "Application Support", "UnityMCP", "instances");
                case RuntimePlatform.LinuxEditor:
                    string xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
                    string baseDir = string.IsNullOrEmpty(xdg) ? Path.Combine(Home(), ".local", "state") : xdg;
                    return Path.Combine(baseDir, "UnityMCP", "instances");
                default: // WindowsEditor (and anything else)
                    string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                    if (string.IsNullOrEmpty(local)) local = Path.Combine(Home(), "AppData", "Local");
                    return Path.Combine(local, "UnityMCP", "instances");
            }
        }

        // The payload returned by the GET health check and the "identity" command: enough for an MCP
        // server to confirm which instance answers a given port.
        public static object Identity(int port)
        {
            return new
            {
                status = "ok",
                server = "UnityMCP",
                instanceId = InstanceId,
                name = Name,
                projectPath = ProjectPath,
                port,
                pid = CurrentPid(),
                unityVersion = Application.unityVersion,
            };
        }

        // Write (or refresh) this instance's record. Best-effort: a failure here just means MCP
        // servers can't discover us, which is logged but not fatal to the Editor.
        public static void Write(int port)
        {
            try
            {
                string dir = RegistryDir();
                Directory.CreateDirectory(dir);

                var record = new
                {
                    instanceId = InstanceId,
                    name = Name,
                    projectPath = ProjectPath,
                    port,
                    authToken = AuthToken,
                    pid = CurrentPid(),
                    unityVersion = Application.unityVersion,
                    startedAtUtc = DateTime.UtcNow.ToString("o"),
                };

                // Write to a temp file then move into place so a reader never observes a partial file.
                string path = FilePath();
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(record, Formatting.Indented));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UnityMCP] Could not write instance registry record: {e.Message}");
            }
        }

        // Remove this instance's record (on quit, or when there's no reachable server to advertise).
        public static void Delete()
        {
            try
            {
                string path = FilePath();
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UnityMCP] Could not delete instance registry record: {e.Message}");
            }
        }

        private static string FilePath() => Path.Combine(RegistryDir(), InstanceId + ".json");

        private static int CurrentPid()
        {
            try { return Process.GetCurrentProcess().Id; } catch { return 0; }
        }

        private static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        private static string ComputeInstanceId(string projectPath)
        {
            using (var sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(projectPath.ToLowerInvariant()));
                var sb = new StringBuilder(8);
                for (int i = 0; i < 4; i++) sb.Append(hash[i].ToString("x2")); // 8 hex chars is plenty here
                return sb.ToString();
            }
        }

        private static string SafeLeaf(string projectPath)
        {
            string leaf = Path.GetFileName(projectPath.TrimEnd('/'));
            return string.IsNullOrEmpty(leaf) ? "Unity" : leaf;
        }
    }
}
