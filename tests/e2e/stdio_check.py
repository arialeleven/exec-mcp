#!/usr/bin/env python3
"""Black-box check of an exec-mcp binary over stdio, the way an MCP client uses it.

    python3 tests/e2e/stdio_check.py path/to/exec-mcp

Uses a temporary workspace and EXEC_MCP_HOME; exits non-zero on the first failed check.
"""
import json, os, queue, shutil, subprocess, sys, tempfile, threading, time

binary = os.path.abspath(sys.argv[1])
root = tempfile.mkdtemp(prefix="exec-mcp-e2e-")
ws, home = os.path.join(root, "ws"), os.path.join(root, "home")
os.makedirs(ws), os.makedirs(os.path.join(home, "tools"))
env = dict(os.environ, EXEC_MCP_HOME=home)

proc = subprocess.Popen([binary, "--workspace", ws], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=subprocess.DEVNULL, text=True, bufsize=1, env=env)
inbox = queue.Queue()
threading.Thread(target=lambda: [inbox.put(json.loads(l)) for l in proc.stdout], daemon=True).start()
notifications, next_id = [], 0


def rpc(method, params=None, timeout=60):
    global next_id
    next_id += 1
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params or {}}) + "\n")
    proc.stdin.flush()
    end = time.time() + timeout
    while time.time() < end:
        try:
            m = inbox.get(timeout=0.2)
        except queue.Empty:
            continue
        if m.get("id") == next_id and "method" not in m:
            return m
        if "method" in m:
            notifications.append(m["method"])
    raise TimeoutError(method)


def call(name, args):
    r = rpc("tools/call", {"name": name, "arguments": args})["result"]
    return "\n".join(c.get("text", "") for c in r["content"]), bool(r.get("isError"))


failed = 0


def check(label, ok, detail=""):
    global failed
    print(f"[{'ok' if ok else 'FAIL'}] {label}" + (f" — {detail}" if detail and not ok else ""))
    failed += not ok


try:
    init = rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "e2e", "version": "1"}})
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n"); proc.stdin.flush()
    check("initialize + instructions", "exec-mcp" in init["result"].get("instructions", ""))

    names = [t["name"] for t in rpc("tools/list")["result"]["tools"]]
    check("core tools listed", {"Read", "Edit", "Write", "Glob", "Grep", "Bash", "exec"} <= set(names), str(names))
    check("ToolSearch hidden without deferred tools", "ToolSearch" not in names)

    text, err = call("Write", {"file_path": "new.txt", "content": "x\n"})
    check("Write", not err and "Created" in text, text)
    with open(os.path.join(ws, "a.py"), "w") as f:   # created outside exec-mcp: must be Read before Edit
        f.write("def f():\n    return 1\n")
    text, err = call("Edit", {"file_path": "a.py", "old_string": "return 1", "new_string": "return 2"})
    check("Edit needs Read", err and "Read it first" in text, text)
    call("Read", {"file_path": "a.py"})
    text, err = call("Edit", {"file_path": "a.py", "old_string": "return 1", "new_string": "return 2"})
    check("Edit after Read", not err and "Applied" in text, text)
    text, _ = call("Grep", {"pattern": "return 2", "output_mode": "content"})
    check("Grep", "a.py:2:" in text, text)

    if os.name != "nt":
        call("Bash", {"command": "mkdir -p sub && cd sub && export E2E=yes"})
        text, _ = call("Bash", {"command": "pwd; echo $E2E"})
        check("Bash session persists", "/sub" in text and "yes" in text, text)

    text, err = call("exec", {"code": "const r = await core.Read({file_path: 'a.py'}); console.log(r.includes('return 2'))"})
    check("exec core namespace", not err and text.strip() == "true", text)

    if os.name != "nt":
        tool = os.path.join(home, "tools", "e2e_echo.sh")
        with open(tool, "w") as f:
            f.write("#!/bin/sh\n# @tool e2e_echo\n# @description Echo for the e2e check.\n# @param msg string Message\n# @readonly\necho \"got $msg\"\n")
        for _ in range(50):
            if "notifications/tools/list_changed" in notifications:
                break
            rpc("tools/list"); time.sleep(0.1)
        check("script tool hot-reload sends list_changed", "notifications/tools/list_changed" in notifications)
        text, err = call("e2e_echo", {"msg": "hi"})
        check("script tool runs", not err and "got hi" in text, text)
finally:
    proc.stdin.close()
    try:
        proc.wait(5)
    except subprocess.TimeoutExpired:
        proc.kill()
    shutil.rmtree(root, ignore_errors=True)

print("ALL OK" if failed == 0 else f"{failed} check(s) failed")
sys.exit(1 if failed else 0)
