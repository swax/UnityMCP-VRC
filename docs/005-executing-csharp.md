# 005 — Executing LLM C# (the command sandbox)

`execute_editor_command` compiles and runs LLM-authored C# *inside the live Editor*, against the full
Unity API. This doc is how that works end to end — the contract, compilation, execution, and how
results (and the read-only state/inspection tools) are kept from flooding the context window. For the
*caller-facing* C# gotchas (fake null, C# 7.0 limits, persistence, reimport), see the
`UnityScriptingNotes` MCP resource in `unity-mcp-server/src/resources/text/`.

## The contract

The snippet is a full C# file with its own `using` directives. It must define:

```csharp
public class EditorCommand
{
    public static object Execute() { /* ... */ return result; }
}
```

The executor reflects `EditorCommand.Execute()` and invokes it. Its returned `object` becomes the
command's `result`, so hand back plain values / anonymous objects (ids, names, counts, paths), not live
`UnityEngine.Object` references.

## Compilation

`EditorCommandExecutor.CompileAndExecute` picks between two backends, decided by the project's API
compatibility level:

- **Mono's CodeDom `CSharpCodeProvider`** (in-process) on .NET Framework-profile projects — the
  Unity 2022-era / VRChat norm. **Language level is C# 7.0** — newer syntax (switch expressions,
  `using` declarations, ranges, target-typed `new()`, the bare `default` literal) is a hard compile
  error. Workarounds are in the scripting-notes resource.
- **The Editor's own bundled Roslyn `csc`** (out of process) on .NET Standard-profile projects —
  the Unity 6 default, where the CodeDom provider is a stub whose compile methods throw
  `PlatformNotSupportedException`. The first such throw flips the executor to Roslyn for the rest
  of the Editor session (one failed probe, ever). `csc` is resolved from inside the running Editor's
  installation — `Data/DotNetSdkRoslyn/csc.dll` (Unity 2022.3, and Unity 6 before 6000.5) or
  `Data/DotNetSdk/sdk/<version>/Roslyn/bincore/csc.dll` (6000.5+), each run with its sibling
  `dotnet` host so runtime and compiler always match. The snippet compiles to a temp-dir DLL with
  `-langversion:latest` (modern C# is fine here) via a response file, and the executor
  `Assembly.Load`s the bytes so no file stays locked.

Both backends compile against the same auto-discovered reference list, with the same flags:

- **`/nostdlib+ /noconfig`** — the compiler adds *no* implicit references, so we control the full set
  (and sidestep "predefined type defined multiple times").
- **Assembly references are auto-discovered**, not hand-maintained: the executor walks every loaded,
  non-dynamic assembly and references those whose name matches —
  - `UnityEngine*` (all engine modules, incl. `ImageConversion`, `ScreenCapture`),
  - `Unity.*` (packages: TextMeshPro, Burst, …), `VRC*`, `UdonSharp*`, `Basis*`,
  - `Assembly-CSharp` / `Assembly-CSharp-Editor` (the project's own scripts),
  - the .NET base class library: `mscorlib`, `System`, the `System.*` facades, `netstandard`.

  The BCL facades are the subtle part: under `/nostdlib+`, a type whose interface is *type-forwarded*
  through a facade fails with a cryptic `CS1070` unless that facade is referenced — e.g. `HashSet<T>`
  (in mscorlib) implements `ISet<T>`, forwarded via `System.Collections`. Referencing the facades makes
  the whole BCL usable. (Forwarders only redirect, they don't redefine, so they don't trip the
  `/nostdlib+` duplicate-type guard.)

Net effect: a snippet can use any engine / editor / package / project API and the full BCL, with no
reference list to maintain.

## Execution

- **Main thread.** Unity APIs are main-thread-only, so the compiled `Execute()` is invoked through
  `EditorUtilities.RunOnMainThread`, whose queue also **defers while the Editor is compiling** — the
  snippet runs against stable post-compile state, never a half-built domain ([004](004-unity-editor-states.md)).
- **Real exceptions, not reflection plumbing.** Reflection wraps a thrown exception in a
  `TargetInvocationException`; the executor rethrows the *inner* exception (preserving its stack), so
  callers see the actual `MissingComponentException` etc., not the wrapper.
- **Stack traces are trimmed** to the first line to save context.

## Result & log capture

The executor returns a payload:

```
{ result, logs, errors, warnings, executionSuccess, [errorDetails] }
```

For the duration of the one snippet it subscribes to `Application.logMessageReceived` (added/removed
around the call), so `logs` / `warnings` / `errors` are **scoped to that command** — the reliable
"did this snippet fail?" signal, independent of the rolling broadcast buffer that `get_logs` reads. A
compile failure or runtime throw comes back as `executionSuccess: false` with the error attached,
rather than as a transport-level error.

## Keeping output bounded

A command's result is unbounded by nature ("list every GameObject…"), and the read-only tools can also
produce large payloads, so shared backstops apply:

- **Paging.** Any serialized result over ~25k chars is cached server-side and returned page-by-page via
  `get_command_page` (the `pageText` helper; rationale in [002](002-design-decisions.md)). Shared by
  `execute_editor_command`, `get_editor_state`, and `get_object_details`.
- **Source caps (read tools).** `EditorStateReporter` caps listed objects/assets (≤300), hierarchy
  nodes (≤500), and depth (≤8). `InspectorDataReporter` reflects a GameObject's components but caps
  collection previews, bounds recursion depth, and **stubs framework (Unity/BCL) types** while expanding
  user-defined types one level — so an inspection stays readable. Those caps bound *breadth and depth*;
  paging is the byte-level backstop on top.

## Trust model

There is **no sandbox**: a snippet runs with the Editor's full privileges (filesystem, project assets,
arbitrary .NET). That's acceptable because the only thing authoring snippets is the developer's own
Claude session driving their own Editor — the trust boundary is the MCP connection itself, not the C#
layer. The plugin therefore binds only to `127.0.0.1` and requires the random per-Editor-session token
from its local registry record on every POST. This blocks LAN clients and blind browser requests, but
the configured MCP client is still fully trusted; review tool calls sourced from untrusted content.

## See also

- `UnityScriptingNotes` resource — caller-facing C# gotchas (fake null, C# 7.0, persistence, reimport).
- [001 — Tools](001-architecture.md#tools) — the full tool surface.
- [002 — Paging](002-design-decisions.md) — why results are paged rather than re-run.
