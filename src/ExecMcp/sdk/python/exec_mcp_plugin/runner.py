"""Plugin-side half of the exec-mcp stdio protocol (JSON-RPC 2.0, one message per line).

Run by the host as: python3 -u runner.py <entry.py>. stdout is the protocol channel; print() goes to stderr.
"""
from __future__ import annotations

import asyncio
import base64
import importlib.util
import inspect
import itertools
import json
import os
import sys
import traceback

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from exec_mcp_plugin import Plugin, ToolError  # noqa: E402

_proto = sys.stdout
sys.stdout = sys.stderr  # keep stray prints off the protocol channel

try:  # memory limit from the manifest (Linux/macOS)
    import resource
    mb = int(os.environ.get("EXEC_MCP_MEMORY_MB", "0"))
    if mb > 0:
        resource.setrlimit(resource.RLIMIT_AS, (mb * 1024 * 1024 * 4, mb * 1024 * 1024 * 4))
except Exception:
    pass

_ids = itertools.count(1)
_pending: dict[str, asyncio.Future] = {}
_calls: dict[str, asyncio.Task] = {}
_secrets: dict[str, str] = {}
_init: dict = {}
plugin: Plugin | None = None


def send(msg: dict) -> None:
    msg["jsonrpc"] = "2.0"
    _proto.write(json.dumps(msg, default=str) + "\n")
    _proto.flush()


async def host_call(method: str, params: dict):
    rid = f"p{next(_ids)}"
    fut = asyncio.get_running_loop().create_future()
    _pending[rid] = fut
    send({"id": rid, "method": method, "params": params})
    return await fut


class _NS:
    def __init__(self, **kw):
        self.__dict__.update(kw)


class Context:
    """ctx: everything the host gives the plugin. All side effects go through the host and its permission checks."""

    def __init__(self, call_id: str | None, cancelled: asyncio.Event):
        self.config = _init.get("config") or {}
        self.call_id = call_id
        self.cancelled = cancelled
        p = lambda d: {**d, "callId": call_id}  # noqa: E731
        self.secrets = _NS(get=lambda name: _secrets.get(name))
        self.fs = _NS(
            read_text=lambda path: _then(host_call("host/fs.read", p({"path": path})), lambda r: r["text"]),
            read_bytes=lambda path: _then(host_call("host/fs.read", p({"path": path, "encoding": "base64"})), lambda r: base64.b64decode(r["base64"])),
            list=lambda path: _then(host_call("host/fs.read", p({"path": path})), lambda r: r["entries"]),
            write_text=lambda path, text: host_call("host/fs.write", p({"path": path, "text": text})),
            write_bytes=lambda path, data: host_call("host/fs.write", p({"path": path, "base64": base64.b64encode(data).decode()})),
        )
        self.cache = _NS(
            get=lambda key: host_call("host/cache.get", p({"key": key})),
            set=lambda key, value, ttl_sec=3600: host_call("host/cache.set", p({"key": key, "value": value, "ttlSec": ttl_sec})),
        )
        self.state = _NS(
            get=lambda key: host_call("host/state.get", p({"key": key})),
            set=lambda key, value: host_call("host/state.set", p({"key": key, "value": value})),
            delete=lambda key: host_call("host/state.delete", p({"key": key})),
        )
        self.artifacts = _NS(write=lambda name, data, mime=None: host_call(
            "host/artifacts.write",
            p({"name": name, "text": data, "mime": mime} if isinstance(data, str) else {"name": name, "base64": base64.b64encode(data).decode(), "mime": mime})))
        self.llm = _NS(complete=lambda prompt, model="small", max_tokens=1024: host_call(
            "host/llm.complete", p({"prompt": prompt, "model": model, "maxTokens": max_tokens}))) if _init.get("permissions", {}).get("llm") else None
        self.log = _NS(
            debug=lambda *a: print("[debug]", *a, file=sys.stderr),
            info=lambda *a: print(*a, file=sys.stderr),
            warn=lambda *a: print("[warn]", *a, file=sys.stderr),
            error=lambda *a: print("[error]", *a, file=sys.stderr),
        )
        self._p = p

    async def fetch(self, url: str, method: str = "GET", headers: dict | None = None, body: str | None = None, timeout_ms: int | None = None) -> dict:
        """Returns {status, url, headers, body | bodyBase64}."""
        return await host_call("host/fetch", self._p({"url": url, "method": method, "headers": headers, "body": body, "timeoutMs": timeout_ms}))

    async def exec(self, cmd: str, args: list[str] | None = None, cwd: str | None = None, stdin: str | None = None, timeout_ms: int | None = None) -> dict:
        return await host_call("host/exec", self._p({"cmd": cmd, "args": args or [], "cwd": cwd, "stdin": stdin, "timeoutMs": timeout_ms}))

    async def call(self, tool: str, input: dict):
        return await host_call("host/call", self._p({"tool": tool, "input": input}))

    def progress(self, message: str | None = None, ratio: float | None = None) -> None:
        if self.call_id:
            send({"method": "$/progress", "params": {"callId": self.call_id, "message": message, "ratio": ratio}})


async def _then(coro, fn):
    return fn(await coro)


def _error(e: BaseException) -> dict:
    if isinstance(e, ToolError):
        return e.to_json()
    if isinstance(e, asyncio.CancelledError):
        return {"code": "TIMEOUT", "message": "cancelled", "retryable": False}
    print(traceback.format_exc(), file=sys.stderr)
    return {"code": "INTERNAL", "message": f"{type(e).__name__}: {e}", "retryable": False}


def _load(entry: str) -> Plugin:
    sys.path.insert(0, os.path.dirname(os.path.abspath(entry)))
    spec = importlib.util.spec_from_file_location("exec_mcp_user_plugin", entry)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    found = getattr(mod, "plugin", None)
    if not isinstance(found, Plugin):
        found = next((v for v in vars(mod).values() if isinstance(v, Plugin)), None)
    if found is None:
        raise RuntimeError(f"{entry} must define `plugin = Plugin()`")
    return found


async def _maybe_await(v):
    return await v if inspect.isawaitable(v) else v


async def handle(method: str, params: dict):
    global plugin, _init
    if method == "initialize":
        _init = params
        plugin = _load(sys.argv[1])
        for name in params.get("permissions", {}).get("secrets", []):
            _secrets[name] = await host_call("host/secrets.get", {"name": name})
        if plugin._setup:
            await _maybe_await(plugin._setup(Context(None, asyncio.Event())))
        return {"tools": [t.describe() for t in plugin.tools.values()]}

    if method == "tools/call":
        t = plugin.tools.get(params["name"]) if plugin else None
        if t is None:
            return {"error": {"code": "NOT_FOUND", "message": f"unknown tool '{params['name']}'", "retryable": False}}
        cancelled = asyncio.Event()
        ctx = Context(params.get("callId"), cancelled)
        data = params.get("input") or {}
        try:
            if t.input_model is not None:
                try:
                    data = t.input_model.model_validate(data)
                except Exception as e:  # pydantic.ValidationError
                    msgs = getattr(e, "errors", lambda: [])()
                    text = "; ".join(f"{'.'.join(map(str, m['loc']))}: {m['msg']}" for m in msgs) or str(e)
                    return {"error": {"code": "INVALID_INPUT", "message": text, "retryable": False}}
            task = asyncio.ensure_future(_maybe_await(t.fn(data, ctx)))
            _calls[params["callId"]] = task
            try:
                out = await task
            except asyncio.CancelledError:
                cancelled.set()
                return {"error": {"code": "TIMEOUT", "message": "cancelled", "retryable": False}}
            if hasattr(out, "model_dump"):
                out = out.model_dump(mode="json")
            text = str(t.format(out)) if t.format else None
            return {"output": out, "text": text}
        except BaseException as e:  # noqa: BLE001
            return {"error": _error(e)}
        finally:
            _calls.pop(params.get("callId"), None)

    if method == "shutdown":
        if plugin and plugin._dispose:
            await _maybe_await(plugin._dispose())
        asyncio.get_running_loop().call_later(0.01, lambda: os._exit(0))
        return None

    raise LookupError(f"method not found: {method}")


async def dispatch(msg: dict) -> None:
    try:
        result = await handle(msg["method"], msg.get("params") or {})
        send({"id": msg["id"], "result": result})
    except BaseException as e:  # noqa: BLE001
        send({"id": msg["id"], "error": {"code": -32000, "message": str(e), "data": _error(e)}})


async def main() -> None:
    loop = asyncio.get_running_loop()
    reader = asyncio.StreamReader(limit=64 * 1024 * 1024)
    await loop.connect_read_pipe(lambda: asyncio.StreamReaderProtocol(reader), sys.stdin)
    while line := await reader.readline():
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except ValueError:
            continue
        if "method" not in msg:  # response to host_call
            fut = _pending.pop(msg.get("id"), None)
            if fut and not fut.done():
                if "error" in msg:
                    d = msg["error"].get("data") or {}
                    fut.set_exception(ToolError(d.get("code", "INTERNAL"), msg["error"].get("message", ""), bool(d.get("retryable"))))
                else:
                    fut.set_result(msg.get("result"))
            continue
        if msg["method"] == "$/cancel":
            task = _calls.get((msg.get("params") or {}).get("callId"))
            if task:
                task.cancel()
            continue
        if "id" in msg:
            asyncio.ensure_future(dispatch(msg))
    if plugin and plugin._dispose:
        await _maybe_await(plugin._dispose())


if __name__ == "__main__":
    asyncio.run(main())
