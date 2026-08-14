# 001 — Architecture (current state)

## Overview

UnityMCP has two halves:

- **Unity Editor plugin** (`UnityMCPPlugin/Editor`, C#) — hosts a small **HTTP server** inside the
  running Editor and executes work against the Unity API.
- **MCP server** (`unity-mcp-server`, Node/TypeScript) — a stdio MCP server that sends the plugin
  one HTTP request per tool call and exposes the tools to Claude.

```
  Claude session A ─▶ MCP server ─┐  ① resolve the target instance via the registry
  Claude session B ─▶ MCP server ─┤  ② POST { type, data } to its port  →  JSON result
  Claude session C ─▶ MCP server ─┘
                                   │
        reads ◀────────────────────┘  ~/…/UnityMCP/instances/<id>.json  (name · port · pid)
        │                                   ▲ each Editor writes its own record
        ▼                                   │
   ┌──────────────────────┐   ┌──────────────────────┐
   │ Unity Editor "A" :p1 │   │ Unity Editor "B" :p2 │   each hosts its own HTTP server
   └──────────────────────┘   └──────────────────────┘   on a dynamic port, self-registers
```

Each Editor hosts its own server (rather than each MCP-server process hosting one) on an OS-assigned
port, and publishes a small record to a shared per-user **registry directory**. An MCP server reads
that directory to discover running Editors and resolve a chosen instance to its port — so many
Editors and many Claude sessions coexist with no fixed-port race, and a session picks which Editor it
drives. The transport is **stateless request/response**; it began as a persistent WebSocket — see the
[historical note](002-design-decisions.md#historical-the-websocket-transport-we-replaced) for why
that's gone.

## Connection model

- **Unity hosts; clients send requests.** Each Editor runs a `TcpListener` on an OS-assigned
  **dynamic** port bound only to IPv4 loopback (`127.0.0.1`); each connection carries **one** HTTP request and is then
  closed. Any number of Claude sessions can drive the same Editor, and several Editors can run at
  once — see [discovery & multiple instances](#discovery--multiple-instances).
- **Request/response.** A tool call is `POST /` with a JSON `{ type, data, authToken }` body; the plugin runs it
  and returns the result as the JSON response body. No id-correlation, no persistent connection — the
  response *is* the reply.
- **Authenticated.** Each Editor generates a random 256-bit token that survives domain reloads but
  rotates on process restart. The token is published only in the per-user registry record; every POST
  is rejected unless it carries that token.
- **Logs are pulled.** The plugin keeps a rolling console-log buffer; `get_logs` reads it on demand
  and `clear_logs` empties it. Nothing is pushed from Unity.

### Wire protocol

| Direction       | Message                                                                   |
| --------------- | ------------------------------------------------------------------------- |
| client → Unity  | `POST / {"type":"executeEditorCommand","data":{"code":"…"},"authToken":"…"}` |
| Unity → client  | `200 OK` + the result payload as the JSON body                            |
| client → Unity  | `POST /` with type `getEditorState` · `takeScreenshot` · `getGameObjectDetails` · `getLogs` · `clearLogs` · `identity` |
| Unity → client  | `401` for a missing/invalid token; `400`/`500` for an unknown type or handler failure |
| local client → Unity | `GET` / `HEAD /` → generic `{status, server}` health response; any other method → `405` |

## Discovery & multiple instances

Several Editors can run at once, so there's no fixed port to dial. Discovery replaces it:

- **Dynamic port.** On start each Editor binds an OS-assigned port and **pins it in `SessionState`**,
  so the port is stable across domain reloads (the process lives on; only managed state resets) and a
  selected instance stays reachable at the same address after a recompile.
- **Self-registration.** The plugin writes a JSON record — `{ instanceId, name, projectPath, port,
  authToken, pid, unityVersion }` — to a shared per-user directory (`InstanceRegistry`). `instanceId` is a short
  stable hash of the project path (also the file name); `name` is the project-folder leaf. The record
  is rewritten on every (re)bind and deleted on quit; a crash leaves it orphaned.
- **The registry directory** is computed identically by both sides, with a `UNITYMCP_REGISTRY_DIR`
  override: `%LOCALAPPDATA%\UnityMCP\instances` (Windows), `~/Library/Application Support/UnityMCP/instances`
  (macOS), `$XDG_RUNTIME_DIR`/`~/.local/state/UnityMCP/instances` (Linux).
- **Liveness is probed, not assumed.** `list_unity_instances` reads the directory and sends an authenticated
  `identity` POST to each port,
  confirming the response's `instanceId` matches the record (so a reused port can't masquerade as the
  dead instance). A refused port means the record is orphaned, and it's deleted (self-heal).
- **Selection is required.** A tool that talks to Unity resolves its target from the call's `instance`
  argument, else the session's selected default (`select_unity_instance`, or the `UNITYMCP_INSTANCE`
  seed). With neither, the call fails rather than guessing — so it can't land in the wrong project.
  Resolution reads the registry per call, so a reload is transparent and a closed Editor gives a clear
  error.

## Transport: hand-rolled HTTP

The plugin reads the request and writes the response by hand over a raw `TcpListener`, rather than
using `System.Net.HttpListener`. This is necessary, not a preference: **Unity's Mono runtime doesn't
reliably implement `HttpListener`** (it's also why the old WebSocket upgrade had to be hand-rolled).
The implementation is deliberately tiny — read the request line + headers to the blank line, read
`Content-Length` bytes of body, write a `Content-Length` response, close (`Connection: close`, one
request per socket). No keep-alive, no chunked encoding.

## Threading model (Unity side)

> The Editor states these mechanisms cope with — compiling, domain reload, play-mode,
> focus/throttling — are covered in [004 — Unity Editor states](004-unity-editor-states.md).

The accept loop and the short-lived per-request handlers run on the **thread pool** (async), off
Unity's editor loop. Anything that touches a Unity API is marshalled to the main thread via
`EditorUtilities.RunOnMainThread`, which:

- enqueues the work and drains the queue on every `EditorApplication.update` tick;
- **defers while Unity is compiling** (`EditorApplication.isCompiling`), so work runs against stable
  post-compile state instead of racing a recompile;
- has a generous timeout (`MainThreadTimeoutMs`, 55s) but fast-fails sooner when it can: a thread-pool
  watchdog notices the loop hasn't ticked for a few seconds (the Editor throttles its loop while
  **unfocused**) and throws an actionable message rather than waiting out the full timeout (focus the
  Editor, or set *Preferences > General > Interaction Mode > No Throttling*).

Because each connection handles a single request and is closed, there are no long-lived per-client
receive loops or send locks to manage.

## Lifecycle & domain reload

- **Start.** `[InitializeOnLoad]` runs the static constructor on editor load and after every domain
  reload; it starts the server via `EditorApplication.delayCall`.
- **Teardown on reload.** A recompile ends in a domain reload that wipes all managed state. On
  `beforeAssemblyReload` the plugin just stops the listener (and closes any socket with a request in
  flight). There's no persistent connection to drain — which is exactly why this is now a non-event
  (see [002](002-design-decisions.md#historical-the-websocket-transport-we-replaced)).
- **Reconnect.** The static constructor restarts the server in the new domain. A client request that
  lands mid-reload gets connection-refused and **retries** (bounded, ~20s) until the server is back,
  so a recompile is a brief pause, not a failure. A request that was already in flight when the socket
  dropped is *not* retried — it may have applied, so the caller is told to check state.
- **Manual restart.** The Debug Window's *Restart Server* button calls `RetryConnection` (stop +
  start) — useful after freeing a port another process held.

## Tools

| Tool                     | What it does                                                             |
| ------------------------ | ------------------------------------------------------------------------ |
| `list_unity_instances`   | Lists running Editors (name, project, `instanceId`) for selection.       |
| `select_unity_instance`  | Sets the default target Editor for the session (calls can override).     |
| `execute_editor_command` | Compiles and runs LLM-authored C# in the Editor; returns result + logs.  |
| `get_editor_state`       | Returns Unity/scene/project state on demand (bounded).                   |
| `get_object_details`     | Returns one GameObject's transform, components, and size/bounds info.    |
| `get_logs`               | Returns recent Unity console logs from the plugin's buffer.              |
| `clear_logs`             | Clears the plugin's buffered console logs.                               |
| `take_screenshot`        | Renders the Scene or game camera to a JPEG/PNG image block.              |
| `get_command_page`       | Fetches a later page of a large result (used automatically).             |

- **execute_editor_command.** The LLM authors full C# — its own `using`s, classes, and functions.
  Assembly references are auto-discovered from all loaded assemblies (UnityEngine modules and
  packages, VRChat/UdonSharp, the .NET base class library, `Assembly-CSharp(-Editor)`, and UnityMCP
  itself), so commands can use any available API with no hand-maintained list. Runs on the main thread
  with scoped log capture; stack traces are trimmed to the first line. Results over ~25k chars are
  **capped** and paged via `get_command_page`. Full model: [005 — Executing LLM C#](005-executing-csharp.md).
- **get_editor_state.** On demand (not a stream). **Capped** for large scenes/projects — ≤300 listed
  objects/assets, ≤500 hierarchy nodes, depth ≤8. Oversized results are paged too.
- **get_object_details.** Resolves a GameObject by name or hierarchy path and reports its transform
  (incl. world-space `lossyScale`), tag, layer, children, and per-component fields/properties via
  reflection — plus extras the inspector can't easily give (Renderer world-space bounds, mesh vertex
  counts, shared mesh/material names). Caps collection previews and recursion depth; oversized results
  are paged.
- **take_screenshot.** Renders the Scene view (default) or the game camera to a base64 JPEG/PNG image
  block. Runs on the main thread; subject to the same unfocused-Editor throttling as other calls.
- **get_logs / clear_logs.** Read from / empty the plugin's rolling log buffer (~1000 entries).
- **get_command_page.** Pulls later slices of a cached oversized result by token + offset. Reads only
  the MCP server's in-memory cache, so it's the one tool that works even while Unity is unreachable.

## MCP server (client) internals

- **Discovery & routing.** `communication/registry.ts` reads the registry directory, probes liveness,
  and resolves a name/id to a record; `ConnectionPool` holds one connection per target instance;
  `session.ts` remembers the selected default. `index.ts` resolves each call's target (call arg →
  session default → error) before dispatching, and centrally injects the required `comment` and
  optional `instance` arguments into every Unity-talking tool's schema.
- `communication/UnityConnection.ts` sends each tool call as an HTTP POST to **its instance's** base
  URL and returns the JSON response. `sendRequest(type, data, timeoutMs)` aborts after `timeoutMs` and
  **retries a refused connection** (bounded, ~20s — `connectWaitMs`), so a call issued during a domain
  reload pauses for the bounce instead of failing. A connection *reset* mid-request is surfaced as
  *may have applied — check state* rather than retried.
- **Lifetime.** Nothing keeps the process alive but its stdio transport, so it exits when the client
  (Claude) goes away — there's no background reconnect timer, hence no lingering "zombie" servers.
- **Resources:** files in `unity-mcp-server/src/resources/text/` are copied into the build and exposed
  as MCP resources (`file:///<name>`).

## Component / file map

**Plugin (`UnityMCPPlugin/Editor`)**

| File                      | Responsibility                                                            |
| ------------------------- | ------------------------------------------------------------------------- |
| `UnityMCPConnection.cs`   | Server lifecycle (dynamic-port bind/accept/teardown), request dispatch, log buffer, debug-window properties. |
| `InstanceRegistry.cs`     | This instance's identity (id/name/project) and the registry record it writes/deletes for discovery. |
| `ClientConnection.cs`     | One HTTP request/response exchange (read request, write response) over a raw socket. |
| `EditorCommandExecutor.cs`| Compile + run LLM C#, scoped log capture, result payload.                |
| `EditorStateReporter.cs`  | Build the bounded editor-state payload.                                  |
| `InspectorDataReporter.cs`| Build a GameObject's component/inspection payload (bounded).             |
| `ScreenshotCapturer.cs`   | Render the Scene/game camera to a base64 image.                          |
| `EditorUtilities.cs`      | Main-thread queue / `RunOnMainThread`, compile-defer, throttle hint.     |
| `UnityMCPWindow.cs`       | Debug Window diagnostics panel.                                          |
| `ScriptTesterWindow.cs`   | Manual command runner for diagnosing C#.                                 |
| `UdonSharpHelper.cs`      | Generate UdonSharp assets from C# (VRChat).                              |

**MCP server (`unity-mcp-server/src`)**

| Path                          | Responsibility                                                       |
| ----------------------------- | ------------------------------------------------------------------- |
| `index.ts`                    | MCP wiring: resolve each call's target instance, list/call tools, list/read resources. |
| `communication/UnityConnection.ts` | HTTP client for one instance: POST a request, return the JSON response, retry a refused connection. |
| `communication/registry.ts`   | Read the instance registry, probe liveness, resolve a name/id to a port (self-heals orphaned records). |
| `communication/ConnectionPool.ts` | One `UnityConnection` per target instance, keyed by `instanceId`.  |
| `session.ts`                  | The session's selected default instance (seeded from `UNITYMCP_INSTANCE`). |
| `tools/*.ts`                  | One file per tool behind a common `Tool` interface (incl. `list`/`select` instances). |
| `tools/commandResultCache.ts` | Shared cache + paging for oversized results.                        |
| `resources/*.ts`              | Text-resource loading.                                              |

## Configuration & limits

| Setting                  | Value / location                                                            |
| ------------------------ | --------------------------------------------------------------------------- |
| Port                     | OS-assigned per Editor, pinned in `SessionState` across reloads; published in the registry |
| Authentication token     | Random 256-bit value per Editor process; survives domain reloads in `SessionState`; never returned by MCP tools |
| Registry directory       | `%LOCALAPPDATA%\UnityMCP\instances` (Win) · `~/Library/Application Support/UnityMCP/instances` (macOS) · `$XDG_RUNTIME_DIR`/`~/.local/state/UnityMCP/instances` (Linux); override `UNITYMCP_REGISTRY_DIR` |
| Default instance         | `UNITYMCP_INSTANCE` (optional) seeds the session's selected instance                       |
| Main-thread timeout      | `55s` (`EditorUtilities.MainThreadTimeoutMs`) — raise with the matching per-tool timeout if needed |
| Editor-state caps        | ≤300 objects/assets, ≤500 hierarchy nodes, depth ≤8                        |
| Result response cap      | ~25,000 chars before paging (`MAX_RESPONSE_CHARS`); cache TTL 5 min, 20 entries |
| Log buffer               | ~1000 entries (plugin side)                                                |
| Connect-retry window     | up to ~20s of retrying a refused connection, then fail with a hint (`connectWaitMs`, client) |

## Known limitations / possible next steps

- `execute_editor_command` intentionally runs arbitrary C# without a sandbox. Loopback binding and a
  per-session token protect the transport, but callers with legitimate MCP access remain fully trusted
  (see [005 — trust model](005-executing-csharp.md#trust-model)).
- Discovery is a shared directory on one machine (no cross-host discovery), and liveness is a probe —
  a crashed Editor's record lingers until the next `list_unity_instances` self-heals it.
- Instance `name` is the project-folder leaf and can collide across unrelated projects; `instanceId`
  (a hash of the project path) disambiguates, and selecting by a colliding bare name is rejected.
- Logs are pulled, not streamed — fine for how Claude uses them; a long-poll endpoint could add
  near-real-time if ever needed.
