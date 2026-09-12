import { promises as fs } from "fs";
import * as os from "os";
import * as path from "path";

// Discovery of running Unity Editors.
//
// Each Editor hosts its own HTTP server on a dynamic port and drops a JSON record into a shared
// per-user directory (see the plugin's InstanceRegistry.cs). This module reads that directory to
// enumerate instances and to resolve a chosen name/id to a port. There's no central broker: the
// directory IS the rendezvous, so any number of MCP processes and Editors coexist without
// coordinating - each Editor writes its own file, each MCP only reads.
//
// The directory path and field names MUST stay in sync with the plugin side.

export interface InstanceRecord {
  instanceId: string; // stable short hash of the project path; also the file name
  name: string; // project folder leaf - the human handle (may collide; instanceId disambiguates)
  projectPath: string;
  port: number;
  authToken: string;
  pid?: number;
  unityVersion?: string;
  startedAtUtc?: string;
}

export interface LiveInstance extends InstanceRecord {
  live: boolean; // did the port answer with a matching instanceId just now?
}

// The shared discovery directory, computed identically to the plugin. UNITYMCP_REGISTRY_DIR
// overrides it (both sides honor the same variable).
export function registryDir(): string {
  const override = process.env.UNITYMCP_REGISTRY_DIR;
  if (override) return override;

  const home = os.homedir();
  if (process.platform === "win32") {
    const base = process.env.LOCALAPPDATA ?? path.join(home, "AppData", "Local");
    return path.join(base, "UnityMCP", "instances");
  }
  if (process.platform === "darwin") {
    return path.join(home, "Library", "Application Support", "UnityMCP", "instances");
  }
  const base = process.env.XDG_RUNTIME_DIR ?? path.join(home, ".local", "state");
  return path.join(base, "UnityMCP", "instances");
}

export class InstanceRegistry {
  constructor(private readonly dir: string = registryDir()) {}

  // Every parseable record in the directory. A missing directory means "no Editors yet" -> []. Files
  // that are half-written or malformed are skipped rather than throwing.
  async readRecords(): Promise<InstanceRecord[]> {
    let files: string[];
    try {
      files = await fs.readdir(this.dir);
    } catch (err: any) {
      if (err?.code === "ENOENT") return [];
      throw err;
    }

    const records: InstanceRecord[] = [];
    for (const file of files) {
      if (!file.endsWith(".json")) continue;
      try {
        const text = await fs.readFile(path.join(this.dir, file), "utf8");
        const rec = JSON.parse(text) as InstanceRecord;
        if (
          rec &&
          typeof rec.port === "number" &&
          typeof rec.instanceId === "string" &&
          typeof rec.authToken === "string" &&
          rec.authToken.length >= 32
        ) {
          records.push(rec);
        }
      } catch {
        // Unparseable (mid-write) or unreadable - ignore; the next list() will pick it up.
      }
    }
    return records;
  }

  // Records plus a liveness probe. "Live" means the port answered with the SAME instanceId, so we
  // never mistake an unrelated process that reused the port for the instance. A refused connection
  // means nothing is listening -> the record is orphaned (the Editor crashed without cleanup), so we
  // delete it. A timeout is left alone (the Editor may just be compiling).
  async list(): Promise<LiveInstance[]> {
    const records = await this.readRecords();
    return Promise.all(
      records.map(async (rec) => {
        const status = await this.probe(rec);
        if (status === "dead") await this.unlink(rec.instanceId);
        return { ...rec, live: status === "live" };
      }),
    );
  }

  // Resolve a name-or-id to its record for ROUTING, without probing (this is the per-call hot path;
  // a dead target surfaces a clear error from the connection layer instead). Throws a helpful error
  // when nothing matches or a bare name is ambiguous.
  async resolve(key: string): Promise<InstanceRecord> {
    const records = await this.readRecords();

    const byId = records.find((r) => r.instanceId === key);
    if (byId) return byId;

    const byName = records.filter((r) => r.name.toLowerCase() === key.toLowerCase());
    if (byName.length === 1) return byName[0];
    if (byName.length > 1) {
      throw new Error(
        `'${key}' is ambiguous - ${byName.length} instances share that name. ` +
          `Select by instanceId: ${byName.map((r) => `${r.name} [${r.instanceId}]`).join(", ")}.`,
      );
    }

    const known = records.length
      ? records.map((r) => `${r.name} [${r.instanceId}]`).join(", ")
      : "(none registered)";
    throw new Error(
      `No Unity instance '${key}'. Known instances: ${known}. ` +
        "Run list_unity_instances to refresh, then select_unity_instance.",
    );
  }

  private async probe(rec: InstanceRecord): Promise<"live" | "dead" | "unknown"> {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 1500);
    try {
      const res = await fetch(`http://127.0.0.1:${rec.port}/`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          type: "identity",
          data: {},
          authToken: rec.authToken,
        }),
        signal: controller.signal,
      });
      if (!res.ok) return "unknown";
      const body: any = await res.json().catch(() => null);
      if (body && body.instanceId === rec.instanceId) return "live";
      return "unknown"; // answered, but it's a different instance (port was reused)
    } catch (err: any) {
      const code = err?.cause?.code ?? err?.code;
      if (code === "ECONNREFUSED") return "dead";
      return "unknown"; // timeout / busy - don't delete, it may be compiling
    } finally {
      clearTimeout(timer);
    }
  }

  private async unlink(instanceId: string): Promise<void> {
    try {
      await fs.unlink(path.join(this.dir, `${instanceId}.json`));
    } catch {
      // Already gone, or another MCP process beat us to it - either way, fine.
    }
  }
}
