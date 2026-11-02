// @exec-mcp/plugin-sdk — plugin spec v0.1.
// A plugin describes its tools once; the host shows them as MCP tools and as typed functions in the `exec` sandbox.
import { z } from "zod";

export { z };

export const ERROR_CODES = ["INVALID_INPUT", "NOT_FOUND", "PERMISSION_DENIED", "RATE_LIMITED", "UPSTREAM", "TIMEOUT", "INTERNAL"];

/** Error with a machine-readable code. retryable + readOnly/idempotent tools are retried once by the host. */
export class ToolError extends Error {
  constructor({ code = "INTERNAL", message, retryable = false, retryAfterMs } = {}) {
    super(message ?? code);
    this.name = "ToolError";
    this.code = ERROR_CODES.includes(code) ? code : "INTERNAL";
    this.retryable = retryable;
    this.retryAfterMs = retryAfterMs;
  }

  toJSON() {
    return { code: this.code, message: this.message, retryable: this.retryable, retryAfterMs: this.retryAfterMs };
  }
}

/** Declares one tool: `run` returns data, `format` turns it into compact text for the model. */
export function tool(def) {
  if (typeof def?.run !== "function") throw new TypeError("tool(): run(input, ctx) is required");
  if (!def.description) throw new TypeError("tool(): description is required");
  return { ...def, __execMcpTool: true };
}

/** Declares a plugin: { setup?(ctx), tools: { name: tool(...) }, dispose?() }. */
export function definePlugin(def) {
  if (!def || typeof def.tools !== "object") throw new TypeError("definePlugin(): tools is required");
  for (const [name, t] of Object.entries(def.tools)) {
    if (!/^[a-z][a-zA-Z0-9_]*$/.test(name)) throw new TypeError(`tool name '${name}' must match [a-z][a-zA-Z0-9_]*`);
    if (!t?.__execMcpTool) throw new TypeError(`tools.${name} must be created with tool()`);
  }
  return { ...def, __execMcpPlugin: true };
}

/** JSON Schema for a zod schema (or a plain JSON Schema object passed through). */
export function toJsonSchema(schema) {
  if (!schema) return undefined;
  if (typeof schema.safeParse !== "function") return schema;
  const js = z.toJSONSchema(schema, { unrepresentable: "any" });
  delete js.$schema;
  return js;
}
