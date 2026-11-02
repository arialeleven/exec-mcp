from collections import Counter
import asyncio
import re

from exec_mcp_plugin import Plugin, ToolError

plugin = Plugin()


@plugin.tool(
    description="Word, line and top-word statistics for a text or a workspace file.",
    input={
        "type": "object",
        "properties": {
            "text": {"type": "string"},
            "path": {"type": "string", "description": "Workspace file instead of text"},
            "top": {"type": "integer", "minimum": 1, "maximum": 50, "default": 5},
        },
    },
    output={
        "type": "object",
        "properties": {
            "words": {"type": "integer"},
            "lines": {"type": "integer"},
            "top": {"type": "array", "items": {"type": "array", "items": {"type": ["string", "integer"]}}},
        },
        "required": ["words", "lines", "top"],
    },
    annotations={"readOnlyHint": True, "idempotentHint": True},
    examples=[{"input": {"text": "a b a"}}],
    format=lambda o: f"{o['words']} words, {o['lines']} lines; top: " + ", ".join(f"{w}×{n}" for w, n in o["top"]),
)
async def stats(input, ctx):
    text = input.get("text")
    if text is None:
        if "path" not in input:
            raise ToolError("INVALID_INPUT", "pass text or path")
        text = await ctx.fs.read_text(input["path"])
    words = re.findall(r"\w+", text.lower())
    return {"words": len(words), "lines": text.count("\n") + (1 if text and not text.endswith("\n") else 0),
            "top": [list(x) for x in Counter(words).most_common(input.get("top", 5))]}


@plugin.tool(description="Sleep (cancellation demo).", input={"type": "object", "properties": {"ms": {"type": "integer"}}, "required": ["ms"]},
             annotations={"readOnlyHint": True}, examples=[{"input": {"ms": 3000}}])
async def sleep(input, ctx):
    await asyncio.sleep(input["ms"] / 1000)
    return {"slept": input["ms"]}
