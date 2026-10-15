"""Call an InfraPortal MCP tool over HTTP, for sessions where the `infraportal` MCP server is not
connected as a client (scheduled runs, other repos, scripts).

    python -I -X utf8 mcp_call.py --list                       # tool names and one-line descriptions
    python -I -X utf8 mcp_call.py --describe get_promotion     # a tool's full description and input schema
    python -I -X utf8 mcp_call.py <tool> ['<json arguments>']  # call a tool, print its result

Connection comes from DEPLOYMENTS_URL and DEPLOYMENTS_API_KEY (the key is sent as X-Api-Key and
never printed). Output is the tool's text result (compact JSON) on stdout, UTF-8.
Exit codes: 0 ok, 1 the tool reported an error (message on stdout), 2 transport/auth/usage error.
"""
import json
import os
import sys
import urllib.error
import urllib.request

PROTOCOL = "2025-11-25"
sys.stdout.reconfigure(encoding="utf-8")


def fail(message, code=2):
    print(message, file=sys.stderr)
    sys.exit(code)


def endpoint():
    base = os.environ.get("DEPLOYMENTS_URL", "").rstrip("/")
    key = os.environ.get("DEPLOYMENTS_API_KEY", "")
    if not base or not key:
        fail("DEPLOYMENTS_URL and DEPLOYMENTS_API_KEY must both be set.")
    return f"{base}/api/mcp", key


def rpc(method, params=None):
    url, key = endpoint()
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params or {}}).encode()
    req = urllib.request.Request(url, data=body, method="POST", headers={
        "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream",
        "MCP-Protocol-Version": PROTOCOL,
        "X-Api-Key": key,
    })
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            ctype = resp.headers.get("Content-Type", "")
            raw = resp.read().decode("utf-8")
    except urllib.error.HTTPError as e:
        hint = {401: " (key missing, invalid, revoked or expired)", 429: " (rate limited; wait a minute)"}.get(e.code, "")
        fail(f"HTTP {e.code} from {url}{hint}")
    except urllib.error.URLError as e:
        fail(f"Cannot reach {url}: {e.reason}")
    if "text/event-stream" in ctype:
        data = [line[5:].strip() for line in raw.splitlines() if line.startswith("data:")]
        raw = data[-1] if data else "{}"
    message = json.loads(raw)
    if "error" in message:
        fail(f"MCP error {message['error'].get('code')}: {message['error'].get('message')}")
    return message.get("result", {})


def main(argv):
    if not argv or argv[0] in ("-h", "--help"):
        print(__doc__)
        return 0
    if argv[0] in ("--list", "--describe"):
        tools = rpc("tools/list").get("tools", [])
        if argv[0] == "--list":
            for t in sorted(tools, key=lambda t: t["name"]):
                hint = " [read-only]" if (t.get("annotations") or {}).get("readOnlyHint") else ""
                print(f"{t['name']}{hint}: {t.get('description', '').split('. ')[0]}")
            return 0
        match = next((t for t in tools if t["name"] == (argv[1] if len(argv) > 1 else "")), None)
        if match is None:
            fail("Unknown tool. Use --list.")
        print(json.dumps({k: match.get(k) for k in ("name", "description", "inputSchema", "annotations")}, indent=2, ensure_ascii=False))
        return 0

    name = argv[0]
    try:
        args = json.loads(argv[1]) if len(argv) > 1 else {}
    except json.JSONDecodeError as e:
        fail(f"Arguments must be a JSON object: {e}")
    result = rpc("tools/call", {"name": name, "arguments": args})
    print("".join(c.get("text", "") for c in result.get("content", []) if c.get("type") == "text"))
    return 1 if result.get("isError") else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
