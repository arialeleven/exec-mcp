# Any executable can be a plugin: read JSON-RPC lines on stdin, answer on stdout, log to stderr.
import json, sys

TOOLS = [{
    "name": "say",
    "description": "Echo a message back, upper-cased.",
    "inputSchema": {"type": "object", "properties": {"message": {"type": "string"}}, "required": ["message"]},
    "outputSchema": {"type": "object", "properties": {"echo": {"type": "string"}}, "required": ["echo"]},
    "annotations": {"readOnlyHint": True},
    "examples": [{"input": {"message": "hi"}}],
}]

for line in sys.stdin:
    msg = json.loads(line)
    if "id" not in msg or "method" not in msg:
        continue
    m, p = msg["method"], msg.get("params") or {}
    if m == "initialize":
        result = {"tools": TOOLS}
    elif m == "tools/call" and p.get("name") == "say":
        out = {"echo": p["input"]["message"].upper()}
        result = {"output": out, "text": out["echo"]}
    elif m == "tools/call":
        result = {"error": {"code": "NOT_FOUND", "message": f"unknown tool {p.get('name')}", "retryable": False}}
    elif m == "shutdown":
        print(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": None}), flush=True)
        break
    else:
        result = None
    print(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": result}), flush=True)
