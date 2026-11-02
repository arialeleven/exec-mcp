// Plugin-side half of the exec-mcp stdio protocol: JSON-RPC 2.0, one message per line.
// stdout is the protocol channel, so console.* is redirected to stderr (the host log).
import { register } from "node:module";
import { createInterface } from "node:readline";
import { pathToFileURL } from "node:url";

register("./hooks.mjs", import.meta.url);

const out = process.stdout;
for (const level of ["log", "info", "debug", "warn", "error"]) {
  console[level] = (...args) => process.stderr.write(args.map((a) => (typeof a === "string" ? a : JSON.stringify(a))).join(" ") + "\n");
}

const { ToolError, toJsonSchema } = await import("@exec-mcp/plugin-sdk");

const send = (msg) => out.write(JSON.stringify({ jsonrpc: "2.0", ...msg }) + "\n");
const pending = new Map();
let nextId = 1;

function hostCall(method, params) {
  const id = `p${nextId++}`;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    send({ id, method, params });
  });
}

const entry = process.argv[2];
let plugin;
let secrets = {};
let baseCtx;
const calls = new Map();   // callId -> AbortController

function makeCtx(init, callId, signal) {
  const withCall = (p) => ({ ...p, callId });
  const llm = init.permissions?.llm?.length
    ? { complete: (prompt, o = {}) => hostCall("host/llm.complete", withCall({ prompt, model: o.model ?? "small", maxTokens: o.maxTokens ?? 1024 })) }
    : undefined;
  return {
    config: init.config ?? {},
    secrets: { get: (name) => secrets[name] },
    fetch: async (url, opts = {}) => {
      const headers = opts.headers instanceof Headers ? Object.fromEntries(opts.headers) : opts.headers;
      const r = await hostCall("host/fetch", withCall({ url: String(url), method: opts.method, headers, body: opts.body, timeoutMs: opts.timeoutMs }));
      const body = r.body ?? (r.bodyBase64 ? Buffer.from(r.bodyBase64, "base64") : null);
      const res = new Response([101, 204, 205, 304].includes(r.status) ? null : body, { status: r.status, headers: r.headers });
      Object.defineProperty(res, "url", { value: r.url });
      return res;
    },
    fs: {
      readText: async (path) => (await hostCall("host/fs.read", withCall({ path }))).text,
      readBytes: async (path) => Buffer.from((await hostCall("host/fs.read", withCall({ path, encoding: "base64" }))).base64, "base64"),
      list: async (path) => (await hostCall("host/fs.read", withCall({ path }))).entries,
      writeText: (path, text) => hostCall("host/fs.write", withCall({ path, text })),
      writeBytes: (path, data) => hostCall("host/fs.write", withCall({ path, base64: Buffer.from(data).toString("base64") })),
    },
    exec: (cmd, args = [], o = {}) => hostCall("host/exec", withCall({ cmd, args, cwd: o.cwd, stdin: o.stdin, timeoutMs: o.timeoutMs })),
    cache: {
      get: (key) => hostCall("host/cache.get", withCall({ key })),
      set: (key, value, ttlSec) => hostCall("host/cache.set", withCall({ key, value, ttlSec })),
    },
    state: {
      get: (key) => hostCall("host/state.get", withCall({ key })),
      set: (key, value) => hostCall("host/state.set", withCall({ key, value })),
      delete: (key) => hostCall("host/state.delete", withCall({ key })),
    },
    artifacts: {
      write: (name, data, mime) =>
        hostCall("host/artifacts.write", withCall(typeof data === "string" ? { name, text: data, mime } : { name, base64: Buffer.from(data).toString("base64"), mime })),
    },
    llm,
    call: (toolName, input) => hostCall("host/call", withCall({ tool: toolName, input })),
    progress: (p) => callId && send({ method: "$/progress", params: { callId, message: p?.message, ratio: p?.ratio } }),
    log: {
      debug: (...a) => console.debug("[debug]", ...a),
      info: (...a) => console.info(...a),
      warn: (...a) => console.warn("[warn]", ...a),
      error: (...a) => console.error("[error]", ...a),
    },
    signal: signal ?? new AbortController().signal,
  };
}

function zodMessage(err) {
  return err.issues.map((i) => `${i.path.length ? i.path.join(".") + ": " : ""}${i.message}`).join("; ");
}

function toErrorObject(e, signal) {
  if (e instanceof ToolError || e?.name === "ToolError") return e.toJSON ? e.toJSON() : { code: e.code, message: e.message, retryable: !!e.retryable };
  if (signal?.aborted || e?.name === "AbortError") return { code: "TIMEOUT", message: "cancelled", retryable: false };
  if (e?.code === "PERMISSION_DENIED") return { code: "PERMISSION_DENIED", message: e.message, retryable: false };
  return { code: "INTERNAL", message: e?.message ?? String(e), retryable: false };
}

async function handle(msg) {
  const { id, method, params } = msg;
  switch (method) {
    case "initialize": {
      const mod = await import(pathToFileURL(entry).href);
      plugin = mod.default;
      if (!plugin?.__execMcpPlugin) throw new Error(`${entry} must export default definePlugin({...})`);
      for (const name of params.permissions?.secrets ?? []) secrets[name] = await hostCall("host/secrets.get", { name });
      baseCtx = { init: params };
      if (plugin.setup) await plugin.setup(makeCtx(params));
      const tools = Object.entries(plugin.tools).map(([name, t]) => ({
        name,
        description: t.description,
        inputSchema: toJsonSchema(t.input) ?? { type: "object", properties: {} },
        outputSchema: toJsonSchema(t.output),
        annotations: t.annotations,
        cost: t.cost,
        examples: t.examples,
      }));
      return { tools };
    }
    case "tools/call": {
      const t = plugin?.tools?.[params.name];
      if (!t) return { error: { code: "NOT_FOUND", message: `unknown tool '${params.name}'`, retryable: false } };
      const ac = new AbortController();
      calls.set(params.callId, ac);
      try {
        let input = params.input ?? {};
        if (t.input?.safeParse) {
          const parsed = t.input.safeParse(input);
          if (!parsed.success) return { error: { code: "INVALID_INPUT", message: zodMessage(parsed.error), retryable: false } };
          input = parsed.data;
        }
        const ctx = makeCtx(baseCtx.init, params.callId, ac.signal);
        const run = Promise.resolve(t.run(input, ctx));
        // A plugin must listen to ctx.signal; the race keeps the host responsive even if it does not.
        const aborted = new Promise((_, reject) => ac.signal.addEventListener("abort", () => reject(Object.assign(new Error("cancelled"), { name: "AbortError" }))));
        const output = await Promise.race([run, aborted]);
        const text = t.format ? String(t.format(output)) : undefined;
        return { output: output === undefined ? null : output, text };
      } catch (e) {
        return { error: toErrorObject(e, ac.signal) };
      } finally {
        calls.delete(params.callId);
      }
    }
    case "shutdown":
      try { await plugin?.dispose?.(); } finally { setTimeout(() => process.exit(0), 10); }
      return null;
    default:
      throw Object.assign(new Error(`method not found: ${method}`), { rpcCode: -32601 });
  }
}

const rl = createInterface({ input: process.stdin });
rl.on("line", async (line) => {
  if (!line.trim()) return;
  let msg;
  try { msg = JSON.parse(line); } catch { return; }

  if (msg.method === undefined) {
    // Response to one of our host/* calls.
    const p = pending.get(msg.id);
    if (!p) return;
    pending.delete(msg.id);
    if (msg.error) {
      const d = msg.error.data ?? {};
      p.reject(new ToolError({ code: d.code ?? "INTERNAL", message: msg.error.message, retryable: !!d.retryable }));
    } else p.resolve(msg.result);
    return;
  }
  if (msg.method === "$/cancel") { calls.get(msg.params?.callId)?.abort(); return; }
  if (msg.id === undefined) return;

  try {
    send({ id: msg.id, result: await handle(msg) });
  } catch (e) {
    send({ id: msg.id, error: { code: e.rpcCode ?? -32000, message: e?.message ?? String(e), data: toErrorObject(e) } });
  }
});
rl.on("close", async () => {
  try { await plugin?.dispose?.(); } finally { process.exit(0); }
});
