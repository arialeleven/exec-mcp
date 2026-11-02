import { z } from "zod";

export { z };

export type ErrorCode = "INVALID_INPUT" | "NOT_FOUND" | "PERMISSION_DENIED" | "RATE_LIMITED" | "UPSTREAM" | "TIMEOUT" | "INTERNAL";

export declare class ToolError extends Error {
  constructor(o: { code: ErrorCode; message: string; retryable?: boolean; retryAfterMs?: number });
  code: ErrorCode;
  retryable: boolean;
  retryAfterMs?: number;
}

export interface ExecResult { exitCode: number; stdout: string; stderr: string }
export interface ExecOpts { cwd?: string; stdin?: string; timeoutMs?: number }

export interface ScopedFs {
  readText(path: string): Promise<string>;
  readBytes(path: string): Promise<Uint8Array>;
  list(path: string): Promise<string[]>;
  writeText(path: string, text: string): Promise<{ path: string }>;
  writeBytes(path: string, data: Uint8Array): Promise<{ path: string }>;
}

export interface KV {
  get<T = unknown>(key: string): Promise<T | undefined>;
  set(key: string, value: unknown): Promise<void>;
  delete(key: string): Promise<void>;
}

export interface Logger {
  debug(...a: unknown[]): void;
  info(...a: unknown[]): void;
  warn(...a: unknown[]): void;
  error(...a: unknown[]): void;
}

export interface PluginContext<Config = Record<string, unknown>> {
  /** Validated against manifest.config, defaults applied. */
  config: Config;
  /** Only secrets declared in the manifest. Values are redacted from all output. */
  secrets: { get(name: string): string | undefined };
  /** fetch through the host: allowlist, timeouts, 5 MB size limit, redirects re-checked. */
  fetch(url: string | URL, init?: { method?: string; headers?: Record<string, string>; body?: string; timeoutMs?: number }): Promise<Response>;
  /** read/write within permissions.fs (${workspace}, ${cache}, ${artifacts}, ${plugin}, ${home}). */
  fs: ScopedFs;
  /** Runs a command from permissions.exec without a shell. */
  exec(cmd: string, args?: string[], o?: ExecOpts): Promise<ExecResult>;
  cache: { get<T = unknown>(k: string): Promise<T | undefined>; set(k: string, v: unknown, ttlSec: number): Promise<void> };
  /** Persistent per-plugin key-value storage. */
  state: KV;
  artifacts: { write(name: string, data: string | Uint8Array, mime?: string): Promise<{ path: string; uri: string }> };
  /** Present only when permissions.llm is non-empty. */
  llm?: { complete(prompt: string, o: { model: "small" | "main"; maxTokens: number }): Promise<string> };
  /** Tools of plugins listed in dependsOn, as "ns.tool". */
  call(tool: string, input: unknown): Promise<unknown>;
  progress(p: { message?: string; ratio?: number }): void;
  /** Goes to the host log, never into the model's context. */
  log: Logger;
  /** Cancellation and timeout; the plugin must listen to it. */
  signal: AbortSignal;
}

export interface ToolAnnotations {
  title?: string;
  readOnlyHint?: boolean;
  destructiveHint?: boolean;
  idempotentHint?: boolean;
  openWorldHint?: boolean;
}

export interface ToolDef<I extends z.ZodType, O extends z.ZodType> {
  description: string;
  input: I;
  output?: O;
  annotations?: ToolAnnotations;
  cost?: { latencyMs?: number };
  examples?: { input: z.input<I> }[];
  run(input: z.output<I>, ctx: PluginContext<any>): Promise<z.output<O>> | z.output<O>;
  /** Compact text for the model; the structured value goes to structuredContent and the sandbox. */
  format?(output: z.output<O>): string;
}

export declare function tool<I extends z.ZodType, O extends z.ZodType = z.ZodUnknown>(def: ToolDef<I, O>): ToolDef<I, O>;

export declare function definePlugin(def: {
  setup?(ctx: PluginContext<any>): Promise<void> | void;
  tools: Record<string, ToolDef<any, any>>;
  dispose?(): Promise<void> | void;
}): unknown;
