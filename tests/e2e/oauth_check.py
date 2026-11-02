"""Checks a deployed exec-mcp HTTP endpoint the way claude.ai connects to it.

    python3 tests/e2e/oauth_check.py https://mcp.example.com <owner-password>

Runs: 401 + resource_metadata, discovery, DCR, consent with the password, PKCE token exchange,
refresh rotation, and MCP initialize / tools/list / tools/call over Streamable HTTP.
With --anthropic-only the MCP steps only work from Anthropic's range or loopback.
"""
import base64, hashlib, json, os, re, ssl, sys, urllib.error, urllib.parse, urllib.request

base, password = sys.argv[1].rstrip("/"), sys.argv[2]
ctx = ssl.create_default_context()


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k):
        return None


opener = urllib.request.build_opener(NoRedirect, urllib.request.HTTPSHandler(context=ctx))


def http(method, url, data=None, headers=None, form=False):
    body = None
    headers = dict(headers or {})
    if data is not None:
        if form:
            body = urllib.parse.urlencode(data).encode()
            headers["Content-Type"] = "application/x-www-form-urlencoded"
        else:
            body = json.dumps(data).encode()
            headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=body, headers=headers, method=method)
    try:
        r = opener.open(req, timeout=60)
        return r.status, dict(r.headers), r.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), e.read().decode()


def step(name, ok, info=""):
    print(f"[{'ok' if ok else 'FAIL'}] {name} {info}")
    if not ok:
        sys.exit(1)


# 1. Unauthenticated MCP call -> 401 with resource_metadata
st, h, _ = http("POST", base + "/mcp", {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}},
                {"Accept": "application/json, text/event-stream"})
www = h.get("WWW-Authenticate", "")
step("401 + resource_metadata", st == 401 and "resource_metadata" in www, www)
meta_url = re.search(r'resource_metadata="([^"]+)"', www).group(1)

# 2. Discovery
st, _, b = http("GET", meta_url); prm = json.loads(b)
step("protected resource metadata", st == 200 and prm["resource"] == base + "/mcp", prm["resource"])
st, _, b = http("GET", prm["authorization_servers"][0] + "/.well-known/oauth-authorization-server"); asm = json.loads(b)
step("authorization server metadata", "S256" in asm["code_challenge_methods_supported"], asm["registration_endpoint"])

# 3. DCR
st, _, b = http("POST", asm["registration_endpoint"], {"client_name": "Claude", "redirect_uris": ["https://claude.ai/api/mcp/auth_callback"],
                                                       "grant_types": ["authorization_code", "refresh_token"], "token_endpoint_auth_method": "none"})
client = json.loads(b); step("DCR", st == 201, client["client_id"])
st, _, b = http("POST", asm["registration_endpoint"], {"client_name": "evil", "redirect_uris": ["https://evil.example/cb"]})
step("DCR rejects foreign redirect", st == 400)

# 4. Authorize: page, wrong password, right password
verifier = base64.urlsafe_b64encode(os.urandom(32)).rstrip(b"=").decode()
challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
params = {"response_type": "code", "client_id": client["client_id"], "redirect_uri": "https://claude.ai/api/mcp/auth_callback",
          "code_challenge": challenge, "code_challenge_method": "S256", "state": "xyz", "scope": "mcp", "resource": base + "/mcp"}
st, _, page = http("GET", asm["authorization_endpoint"] + "?" + urllib.parse.urlencode(params))
step("login page", st == 200 and "Пароль" in page)
st, _, _ = http("POST", asm["authorization_endpoint"], {**params, "password": "wrong", "action": "allow"}, form=True)
step("wrong password rejected", st == 401)
st, h, _ = http("POST", asm["authorization_endpoint"], {**params, "password": password, "action": "allow"}, form=True)
loc = h.get("Location", "")
q = urllib.parse.parse_qs(urllib.parse.urlparse(loc).query)
step("redirect with code", st == 302 and q.get("state") == ["xyz"] and "code" in q, loc.split("?")[0])

# 5. Token (bad verifier, then good)
tok = {"grant_type": "authorization_code", "code": q["code"][0], "redirect_uri": params["redirect_uri"], "client_id": client["client_id"]}
st, _, b = http("POST", asm["token_endpoint"], {**tok, "code_verifier": "x" * 43}, form=True)
step("bad PKCE rejected (and code burned)", st == 400 and "invalid_grant" in b)
# the code was consumed; do a fresh authorization
st, h, _ = http("POST", asm["authorization_endpoint"], {**params, "password": password, "action": "allow"}, form=True)
code = urllib.parse.parse_qs(urllib.parse.urlparse(h["Location"]).query)["code"][0]
st, _, b = http("POST", asm["token_endpoint"], {**tok, "code": code, "code_verifier": verifier}, form=True)
t = json.loads(b); step("token issued", st == 200 and t["token_type"] == "Bearer", f"expires_in={t['expires_in']}")

# 6. Refresh rotation
st, _, b = http("POST", asm["token_endpoint"], {"grant_type": "refresh_token", "refresh_token": t["refresh_token"], "client_id": client["client_id"]}, form=True)
t2 = json.loads(b); step("refresh", st == 200)
st, _, b = http("POST", asm["token_endpoint"], {"grant_type": "refresh_token", "refresh_token": t["refresh_token"]}, form=True)
step("old refresh token rejected", st == 400 and "invalid_grant" in b)

# 7. MCP over Streamable HTTP with the bearer token
H = {"Authorization": "Bearer " + t2["access_token"], "Accept": "application/json, text/event-stream"}


def rpc(msg, sid=None):
    hh = dict(H)
    if sid: hh["Mcp-Session-Id"] = sid
    st, h, b = http("POST", base + "/mcp", msg, hh)
    data = [json.loads(l[5:]) for l in b.splitlines() if l.startswith("data:")] or ([json.loads(b)] if b.strip().startswith("{") else [])
    return st, h, data


st, h, d = rpc({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "t", "version": "1"}}})
sid = h.get("Mcp-Session-Id") or h.get("mcp-session-id")
step("initialize", st == 200 and d and "instructions" in d[0]["result"], f"session={sid[:8] if sid else None}…")
rpc({"jsonrpc": "2.0", "method": "notifications/initialized"}, sid)
st, _, d = rpc({"jsonrpc": "2.0", "id": 2, "method": "tools/list"}, sid)
names = [x["name"] for x in d[0]["result"]["tools"]]
step("tools/list", "Bash" in names, ", ".join(names))
st, _, d = rpc({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "Glob", "arguments": {"pattern": "*"}}}, sid)
step("tools/call Glob", st == 200, d[0]["result"]["content"][0]["text"].replace("\n", " | "))
st, _, _ = http("POST", base + "/mcp", {"jsonrpc": "2.0", "id": 4, "method": "tools/list"}, {"Authorization": "Bearer nope", "Accept": "application/json, text/event-stream"})
step("bad token rejected", st == 401)
print("ALL OK")
