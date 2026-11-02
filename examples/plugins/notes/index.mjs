import { definePlugin, tool, z, ToolError } from "@exec-mcp/plugin-sdk";

export default definePlugin({
  tools: {
    add: tool({
      description: "Save a note. Returns its id.",
      input: z.object({ text: z.string().min(1), tags: z.array(z.string()).optional() }),
      output: z.object({ id: z.number(), count: z.number() }),
      annotations: { idempotentHint: false },
      examples: [{ input: { text: "hello" } }],
      async run({ text, tags }, ctx) {
        const notes = (await ctx.state.get("notes")) ?? [];
        if (notes.length >= ctx.config.maxNotes) throw new ToolError({ code: "INVALID_INPUT", message: `limit of ${ctx.config.maxNotes} notes reached` });
        const id = (notes.at(-1)?.id ?? 0) + 1;
        notes.push({ id, text, tags: tags ?? [] });
        await ctx.state.set("notes", notes);
        return { id, count: notes.length };
      },
      format: ({ id, count }) => `saved #${id} (${count} notes)`,
    }),

    list: tool({
      description: "List notes, optionally filtered by tag.",
      input: z.object({ tag: z.string().optional() }),
      output: z.object({ notes: z.array(z.object({ id: z.number(), text: z.string(), tags: z.array(z.string()) })) }),
      annotations: { readOnlyHint: true },
      examples: [{ input: {} }],
      async run({ tag }, ctx) {
        const notes = (await ctx.state.get("notes")) ?? [];
        return { notes: tag ? notes.filter((n) => n.tags.includes(tag)) : notes };
      },
      format: ({ notes }) => notes.map((n) => `#${n.id} ${n.text}${n.tags.length ? ` [${n.tags.join(", ")}]` : ""}`).join("\n") || "no notes",
    }),

    wordCount: tool({
      description: "Count words in a workspace file (demo of ctx.fs and ctx.cache).",
      input: z.object({ path: z.string() }),
      output: z.object({ words: z.number(), cached: z.boolean() }),
      annotations: { readOnlyHint: true, idempotentHint: true },
      async run({ path }, ctx) {
        const key = `wc:${path}`;
        const hit = await ctx.cache.get(key);
        if (hit !== undefined && hit !== null) return { words: hit, cached: true };
        const text = await ctx.fs.readText(path);
        const words = text.split(/\s+/).filter(Boolean).length;
        await ctx.cache.set(key, words, 60);
        return { words, cached: false };
      },
      format: ({ words, cached }) => `${words} words${cached ? " (cached)" : ""}`,
    }),

    npmVersion: tool({
      description: "Latest version of an npm package (demo of ctx.fetch with a host allowlist).",
      input: z.object({ name: z.string(), registry: z.string().optional() }),
      output: z.object({ version: z.string() }),
      annotations: { readOnlyHint: true, openWorldHint: true },
      async run({ name, registry }, ctx) {
        const res = await ctx.fetch(`${registry ?? "https://registry.npmjs.org"}/${encodeURIComponent(name)}/latest`);
        if (!res.ok) throw new ToolError({ code: res.status === 404 ? "NOT_FOUND" : "UPSTREAM", message: `registry returned ${res.status}`, retryable: res.status >= 500 });
        return { version: (await res.json()).version };
      },
      format: ({ version }) => version,
    }),

    exportAll: tool({
      description: "Write all notes to an artifact file and return its path.",
      input: z.object({}),
      output: z.object({ path: z.string() }),
      async run(_, ctx) {
        const notes = (await ctx.state.get("notes")) ?? [];
        const { path } = await ctx.artifacts.write("notes.md", notes.map((n) => `- ${n.text}`).join("\n") + "\n", "text/markdown");
        return { path };
      },
      format: ({ path }) => `exported to ${path}`,
    }),

    gitHead: tool({
      description: "Current git commit of the workspace (demo of ctx.exec).",
      input: z.object({}),
      output: z.object({ commit: z.string() }),
      annotations: { readOnlyHint: true },
      async run(_, ctx) {
        const r = await ctx.exec("git", ["rev-parse", "--short", "HEAD"]);
        if (r.exitCode !== 0) throw new ToolError({ code: "NOT_FOUND", message: r.stderr.trim() || "not a git repository" });
        return { commit: r.stdout.trim() };
      },
      format: ({ commit }) => commit,
    }),

    slow: tool({
      description: "Waits for the given milliseconds (demo of cancellation via ctx.signal).",
      input: z.object({ ms: z.number().int().min(0).max(60000) }),
      output: z.object({ waited: z.number() }),
      annotations: { readOnlyHint: true },
      examples: [{ input: { ms: 3000 } }],
      run: ({ ms }, ctx) =>
        new Promise((resolve, reject) => {
          const t = setTimeout(() => resolve({ waited: ms }), ms);
          ctx.signal.addEventListener("abort", () => { clearTimeout(t); reject(new ToolError({ code: "TIMEOUT", message: "cancelled" })); });
        }),
    }),
  },
});
