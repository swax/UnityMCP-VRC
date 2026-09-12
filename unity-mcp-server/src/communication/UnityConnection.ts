// The Unity Editor hosts a tiny HTTP endpoint; this MCP server sends one request per tool call.
//
// This used to be a persistent WebSocket client, but keeping a long-lived socket alive across
// Unity's frequent domain reloads is what generated nearly all the connection-handling complexity
// (a reconnect loop, an id-keyed pending map, ride-out-reload waits, and a plugin-side teardown that
// kept stalling the reload). Stateless request/response collapses all of that into "POST a command,
// read the response" plus a bounded retry when the Editor is briefly unreachable (mid-reload or not
// started yet). Unity still owns the one port, so any number of MCP servers share one Editor.

// The minimal surface a tool needs: send one request and await its response. Tools receive this
// (not the concrete UnityConnection) so a per-call view can transparently stamp every request with
// the caller's comment - see UnityConnection.forComment.
export interface RequestSender {
  sendRequest(type: string, data: any, timeoutMs?: number): Promise<any>;
}

export class UnityConnection implements RequestSender {
  private readonly baseUrl: string;
  private readonly authToken: string;
  // Human-facing name of the target instance, used in error messages (e.g. "Garibaldi").
  private readonly label: string;
  private shuttingDown = false;

  // How long to keep retrying a refused connection before giving up. Covers a domain-reload bounce
  // (the listener is down for a beat) or the Editor still starting; past it we assume it's down.
  private readonly connectWaitMs = 20_000;

  // baseUrl is resolved per target instance (each Editor hosts on its own dynamic port); label is
  // that instance's name, for readable error messages.
  constructor(opts: { baseUrl: string; authToken: string; label?: string }) {
    this.baseUrl = opts.baseUrl;
    this.authToken = opts.authToken;
    this.label = opts.label ?? opts.baseUrl;
  }

  // Send a tool request to Unity and resolve with its JSON response payload. POSTs the command plus
  // the per-Editor-session authentication token;
  // the Editor runs the work on its main thread and returns the result as the response body.
  //
  // Failure handling mirrors what the old WebSocket layer did, inferred from the HTTP outcome:
  // - connection *refused* (Editor down or mid-reload, request never arrived) -> retry up to
  //   connectWaitMs, so a reload is a brief pause rather than a failure;
  // - connection *reset* mid-request (Editor likely started reloading after we sent) -> NOT retried,
  //   surfaced as "may have applied" since a non-idempotent command might have run;
  // - timeout -> surfaced with the usual unfocused/busy hint.
  // A per-call view of this connection that stamps every request it sends with `comment` - the
  // operator-facing "why this call" note shown live in the Unity debug window's recent-calls panel.
  // Tools call sendRequest exactly as before; the comment rides along in the envelope without each
  // tool having to thread it through.
  public forComment(comment?: string): RequestSender {
    return {
      sendRequest: (type, data, timeoutMs) =>
        this.sendRequest(type, data, timeoutMs, comment),
    };
  }

  public async sendRequest(
    type: string,
    data: any,
    timeoutMs: number = 60_000,
    comment?: string,
  ): Promise<any> {
    if (this.shuttingDown) throw new Error("MCP server shutting down.");

    const deadline = Date.now() + this.connectWaitMs;
    let attempt = 0;

    while (true) {
      attempt++;
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), timeoutMs);
      try {
        const res = await fetch(this.baseUrl, {
          method: "POST",
          headers: { "content-type": "application/json" },
          // comment is undefined for internal/legacy callers; JSON.stringify drops it, so Unity
          // simply sees no comment key and the envelope is unchanged.
          body: JSON.stringify({
            type,
            data,
            comment,
            authToken: this.authToken,
          }),
          signal: controller.signal,
        });

        if (!res.ok) {
          const text = await res.text().catch(() => "");
          throw new Error(
            `Unity returned ${res.status} ${res.statusText}${text ? `: ${text}` : ""}`,
          );
        }

        return await res.json();
      } catch (err: any) {
        // Refused = the request never reached Unity, so it's safe to retry while we wait out a
        // reload/startup. Anything else (timeout, reset, HTTP error) is surfaced as-is.
        if (this.isConnRefused(err) && !this.shuttingDown && Date.now() < deadline) {
          await delay(Math.min(500 * attempt, 2000));
          continue;
        }
        throw this.describe(err, timeoutMs);
      } finally {
        clearTimeout(timer);
      }
    }
  }

  public close(): void {
    this.shuttingDown = true;
  }

  private isConnRefused(err: any): boolean {
    return (err?.cause?.code ?? err?.code) === "ECONNREFUSED";
  }

  // Turn a fetch failure into an actionable message, preserving the substrings the tools key off
  // ("timed out", and the reset -> "may have applied" wording).
  private describe(err: any, timeoutMs: number): Error {
    if (err?.name === "AbortError") {
      return new Error(
        `Request timed out after ${Math.round(timeoutMs / 1000)}s. The Unity Editor may be ` +
          "unfocused, compiling, or busy - focus it (or set Interaction Mode to No Throttling) and retry.",
      );
    }
    const code = err?.cause?.code ?? err?.code;
    if (code === "ECONNREFUSED") {
      return new Error(
        `Unity instance '${this.label}' isn't reachable at ${this.baseUrl} - is that Editor still ` +
          "running with the UnityMCP plugin? (It may be mid domain-reload; it should return within a " +
          "few seconds. If it was closed, run list_unity_instances and re-select.)",
      );
    }
    if (code === "ECONNRESET" || code === "UND_ERR_SOCKET") {
      return new Error(
        `Unity instance '${this.label}' dropped the connection mid-request (it likely began a domain ` +
          "reload). The command may or may not have applied - check the editor/scene state before retrying.",
      );
    }
    return err instanceof Error ? err : new Error(String(err));
  }
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// A RequestSender that refuses to send. Handed to contexts that must expose the RequestSender shape
// but have no Unity instance to talk to: static resources, and tools with requiresInstance === false
// (list/select/get_command_page). Calling it is a programming error, so it throws.
export function unusableSender(reason: string): RequestSender {
  return {
    sendRequest: async () => {
      throw new Error(reason);
    },
  };
}
