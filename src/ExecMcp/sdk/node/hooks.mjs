// Lets plugins `import ... from "@exec-mcp/plugin-sdk"` (and zod) without installing anything.
const sdk = new URL("./index.mjs", import.meta.url).href;

export async function resolve(specifier, context, next) {
  if (specifier === "@exec-mcp/plugin-sdk") return { url: sdk, shortCircuit: true };
  try {
    return await next(specifier, context);
  } catch (e) {
    if (specifier === "zod" || specifier.startsWith("zod/")) return next(specifier, { ...context, parentURL: sdk });
    throw e;
  }
}
