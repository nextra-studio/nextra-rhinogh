"""Minimal streamable-HTTP MCP client (used to benchmark other Rhino MCP servers on the same tasks).
usage: python tools/mcp_http.py <url> list | call <tool> '<json args>'
Prints the response and a token estimate (text chars/4, images w*h/750); appends to test/11_compare/calls_<host>.jsonl
"""
import json, sys, os, time, urllib.request, base64, io

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


class Client:
    def __init__(self, url):
        self.url, self.sid, self.n = url, None, 0
        self.req("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "bench", "version": "1"}})
        self.req("notifications/initialized", None, notify=True)

    def req(self, method, params, notify=False):
        body = {"jsonrpc": "2.0", "method": method}
        if params is not None: body["params"] = params
        if not notify: self.n += 1; body["id"] = self.n
        h = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
        if self.sid: h["Mcp-Session-Id"] = self.sid
        r = urllib.request.urlopen(urllib.request.Request(self.url, data=json.dumps(body).encode(), headers=h), timeout=300)
        self.sid = r.headers.get("Mcp-Session-Id") or self.sid
        raw = r.read().decode("utf-8")
        if notify or not raw.strip(): return None
        if raw.lstrip().startswith("{"): return json.loads(raw)
        for line in raw.splitlines():          # SSE: take the data line carrying our id
            if line.startswith("data:"):
                m = json.loads(line[5:])
                if m.get("id") == body.get("id"): return m
        return None


def img_tokens(b64):
    try:
        from PIL import Image
        w, h = Image.open(io.BytesIO(base64.b64decode(b64))).size
        return w * h // 750, (w, h)
    except Exception:
        return 0, None


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    url, cmd = sys.argv[1], sys.argv[2]
    c = Client(url)
    if cmd == "list":
        tools = c.req("tools/list", {})["result"]["tools"]
        s = json.dumps(tools)
        print(f"{len(tools)} tools, schema {len(s)} chars ≈ {len(s)//4} tokens")
        print([t["name"] for t in tools])
        os.makedirs(os.path.join(REPO, "test", "11_compare"), exist_ok=True)
        open(os.path.join(REPO, "test", "11_compare", "tools_" + url.split(":")[2].split("/")[0] + ".json"), "w").write(json.dumps(tools, indent=1))
        sys.exit(0)
    tool, args = sys.argv[3], json.loads(sys.argv[4]) if len(sys.argv) > 4 else {}
    if args.get("__file"):
        args = json.load(open(args["__file"], encoding="utf-8"))
    t0 = time.time()
    res = c.req("tools/call", {"name": tool, "arguments": args})
    dt = time.time() - t0
    content = (res or {}).get("result", {}).get("content", []) or [{"type": "text", "text": json.dumps(res)}]
    tok_out, text = 0, ""
    for item in content:
        if item.get("type") == "text":
            text += item["text"]; tok_out += len(item["text"]) // 4
        elif item.get("type") == "image":
            t, size = img_tokens(item["data"]); tok_out += t; text += f"\n[image {size}]"
    tok_in = len(json.dumps(args)) // 4
    print(text[:3000] + ("…" if len(text) > 3000 else ""))
    print(f"--- {dt:.2f}s ~in {tok_in} tok / out {tok_out} tok")
    with open(os.path.join(REPO, "test", "11_compare", "calls_mcneel.jsonl"), "a", encoding="utf-8") as f:
        f.write(json.dumps({"tool": tool, "tok_in": tok_in, "tok_out": tok_out, "sec": round(dt, 2), "chars": len(text)}) + "\n")
