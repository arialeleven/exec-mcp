import { definePlugin, tool, z, ToolError } from "@exec-mcp/plugin-sdk";
import pg from "pg";

// Note: pg opens its own TCP socket, so `permissions.net` documents intent but is enforced only for ctx.fetch.
// Run the plugin in a network sandbox if you need hard enforcement.
let pool;

const fmtTable = ({ columns, rows, truncated }) =>
  [columns.join("\t"), ...rows.map((r) => r.map((v) => (v === null ? "NULL" : typeof v === "object" ? JSON.stringify(v) : String(v))).join("\t"))].join("\n") +
  (truncated ? "\n… more rows, add LIMIT or narrow WHERE" : "");

async function readOnly(ctx, fn) {
  const client = await pool.connect();
  const onAbort = () => client.query("SELECT pg_cancel_backend(pg_backend_pid())").catch(() => {});
  ctx.signal.addEventListener("abort", onAbort);
  try {
    await client.query("BEGIN READ ONLY");
    await client.query(`SET LOCAL statement_timeout = ${Number(ctx.config.statementTimeoutMs)}`);
    return await fn(client);
  } catch (e) {
    if (e instanceof ToolError) throw e;
    const conn = /ECONNREFUSED|ENOTFOUND|timeout/i.test(e.message);
    throw new ToolError({
      code: conn ? "UPSTREAM" : "INVALID_INPUT",
      message: conn ? `Postgres is not reachable: ${e.message}. Is the database running?` : e.message,
      retryable: conn,
      retryAfterMs: conn ? 2000 : undefined,
    });
  } finally {
    ctx.signal.removeEventListener("abort", onAbort);
    await client.query("ROLLBACK").catch(() => {});
    client.release();
  }
}

export default definePlugin({
  async setup(ctx) {
    // Initialise clients; the host gives setup 10s.
    pool = new pg.Pool({ connectionString: ctx.secrets.get("DATABASE_URL"), max: 4, connectionTimeoutMillis: 5000 });
  },

  tools: {
    query: tool({
      description: "Run a read-only SQL query. Returns rows as a compact table. Use db.schema first if table names are unknown.",
      input: z.object({
        sql: z.string().min(1),
        limit: z.number().int().min(1).max(1000).optional(),
      }),
      output: z.object({
        columns: z.array(z.string()),
        rows: z.array(z.array(z.unknown())),
        truncated: z.boolean(),
      }),
      annotations: { readOnlyHint: true, idempotentHint: true },
      cost: { latencyMs: 200 },
      examples: [{ input: { sql: "select count(*) from information_schema.tables" } }],

      run: ({ sql, limit }, ctx) =>
        readOnly(ctx, async (client) => {
          const max = limit ?? ctx.config.maxRows;
          const r = await client.query({ text: sql, rowMode: "array" });
          return { columns: r.fields.map((f) => f.name), rows: r.rows.slice(0, max), truncated: r.rows.length > max };
        }),

      // Compact text for the model. The structure goes to structuredContent and to the sandbox.
      format: fmtTable,
    }),

    schema: tool({
      description: "List tables and columns (optionally of one table). Use before writing queries.",
      input: z.object({ table: z.string().optional() }),
      output: z.object({ tables: z.array(z.object({ name: z.string(), columns: z.array(z.string()) })) }),
      annotations: { readOnlyHint: true, idempotentHint: true },
      cost: { latencyMs: 100 },
      run: ({ table }, ctx) =>
        readOnly(ctx, async (client) => {
          const r = await client.query(
            `select table_schema || '.' || table_name as t, column_name || ' ' || data_type as c
               from information_schema.columns
              where table_schema not in ('pg_catalog', 'information_schema') and ($1::text is null or table_name = $1)
              order by 1, ordinal_position`,
            [table ?? null],
          );
          const map = new Map();
          for (const row of r.rows) (map.get(row.t) ?? map.set(row.t, []).get(row.t)).push(row.c);
          return { tables: [...map].map(([name, columns]) => ({ name, columns })) };
        }),
      format: ({ tables }) => tables.map((t) => `${t.name}(${t.columns.join(", ")})`).join("\n") || "no tables",
    }),

    explain: tool({
      description: "Show the query plan (EXPLAIN, no ANALYZE) for a SQL query.",
      input: z.object({ sql: z.string().min(1) }),
      output: z.object({ plan: z.string() }),
      annotations: { readOnlyHint: true, idempotentHint: true },
      run: ({ sql }, ctx) =>
        readOnly(ctx, async (client) => {
          const r = await client.query(`EXPLAIN ${sql}`);
          return { plan: r.rows.map((x) => x["QUERY PLAN"]).join("\n") };
        }),
      format: ({ plan }) => plan,
    }),
  },

  async dispose() {
    await pool?.end();
  },
});
