"""exec-mcp plugin SDK for Python (plugin spec v0.1).

    from exec_mcp_plugin import Plugin, ToolError

    plugin = Plugin()

    @plugin.setup
    async def setup(ctx): ...

    @plugin.tool(
        description="Count words in a text.",
        input={"type": "object", "properties": {"text": {"type": "string"}}, "required": ["text"]},
        annotations={"readOnlyHint": True},
        format=lambda out: f"{out['words']} words",
    )
    async def count(input, ctx):
        return {"words": len(input["text"].split())}

`input`/`output` take a JSON Schema dict or a pydantic model class.
"""
from __future__ import annotations

import re
from typing import Any, Awaitable, Callable, Optional

__all__ = ["Plugin", "ToolError", "ERROR_CODES"]

ERROR_CODES = ("INVALID_INPUT", "NOT_FOUND", "PERMISSION_DENIED", "RATE_LIMITED", "UPSTREAM", "TIMEOUT", "INTERNAL")


class ToolError(Exception):
    """Error with a machine-readable code; retryable errors of read-only tools are retried once by the host."""

    def __init__(self, code: str = "INTERNAL", message: str = "", retryable: bool = False, retry_after_ms: Optional[int] = None):
        super().__init__(message or code)
        self.code = code if code in ERROR_CODES else "INTERNAL"
        self.message = message or code
        self.retryable = retryable
        self.retry_after_ms = retry_after_ms

    def to_json(self) -> dict:
        d = {"code": self.code, "message": self.message, "retryable": self.retryable}
        if self.retry_after_ms is not None:
            d["retryAfterMs"] = self.retry_after_ms
        return d


def _schema(s: Any) -> Optional[dict]:
    if s is None:
        return None
    if isinstance(s, dict):
        return s
    if hasattr(s, "model_json_schema"):  # pydantic v2
        return s.model_json_schema()
    raise TypeError("input/output must be a JSON Schema dict or a pydantic model")


class _Tool:
    def __init__(self, name, fn, description, input, output, annotations, cost, examples, format):
        self.name = name
        self.fn = fn
        self.description = description
        self.input_model = input if hasattr(input, "model_validate") else None
        self.output_model = output if hasattr(output, "model_validate") else None
        self.input_schema = _schema(input) or {"type": "object", "properties": {}}
        self.output_schema = _schema(output)
        self.annotations = annotations
        self.cost = cost
        self.examples = examples
        self.format = format

    def describe(self) -> dict:
        d = {"name": self.name, "description": self.description, "inputSchema": self.input_schema}
        for key, value in (("outputSchema", self.output_schema), ("annotations", self.annotations), ("cost", self.cost), ("examples", self.examples)):
            if value is not None:
                d[key] = value
        return d


class Plugin:
    def __init__(self) -> None:
        self.tools: dict[str, _Tool] = {}
        self._setup: Optional[Callable[[Any], Awaitable[None]]] = None
        self._dispose: Optional[Callable[[], Awaitable[None]]] = None

    def setup(self, fn):
        self._setup = fn
        return fn

    def dispose(self, fn):
        self._dispose = fn
        return fn

    def tool(self, *, description: str, input: Any = None, output: Any = None, annotations: Optional[dict] = None,
             cost: Optional[dict] = None, examples: Optional[list] = None, format: Optional[Callable[[Any], str]] = None,
             name: Optional[str] = None):
        def deco(fn):
            tool_name = name or fn.__name__
            if not re.match(r"^[a-z][a-zA-Z0-9_]*$", tool_name):
                raise ValueError(f"tool name '{tool_name}' must match [a-z][a-zA-Z0-9_]*")
            self.tools[tool_name] = _Tool(tool_name, fn, description, input, output, annotations, cost, examples, format)
            return fn
        return deco
