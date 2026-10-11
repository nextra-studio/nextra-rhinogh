"""Test client for the Nextra server (same wire protocol as the MCP bridge).

usage:  python tools/nx.py <tool> '<json args>' [--timeout 60]
        python tools/nx.py health | reload
Prints the reply text, image paths, and a token estimate (text ≈ chars/4, image ≈ w*h/750).
Every call is appended to test/calls.jsonl for later token accounting.
"""
import json, os, sys, time, urllib.request

APP = os.path.join(os.environ["APPDATA"], "Nextra")
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def _alive(pid):
    try:
        import subprocess
        out = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/NH"], capture_output=True, text=True).stdout
        return "Rhino" in out
    except Exception:
        return True


def server():
    # one file per Rhino process; prefer NEXTRA_PID, else the newest live Rhino (headless/dead processes are skipped)
    import glob
    cands = []
    for p in glob.glob(os.path.join(APP, "server-*.json")):
        try:
            with open(p, encoding="utf-8") as f: s = json.load(f)
            cands.append(s)
        except Exception: pass
    want = os.environ.get("NEXTRA_PID")
    if want: cands = [c for c in cands if str(c.get("pid")) == want]
    wf = os.environ.get("NEXTRA_FILE")
    if wf: cands = [c for c in cands if wf.lower() in (str(c.get("file", "")) + str(c.get("path", ""))).lower()]
    cands = sorted(cands, key=lambda c: c.get("started", ""), reverse=True)
    for c in cands:
        if _alive(c["pid"]): return c
    with open(os.path.join(APP, "server.json"), encoding="utf-8") as f:
        return json.load(f)


def post(path, body, timeout=600):
    s = server()
    req = urllib.request.Request(f"http://127.0.0.1:{s['port']}{path}", data=json.dumps(body).encode(),
                                 headers={"X-Nextra-Token": s["token"], "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def image_tokens(path):
    try:
        from PIL import Image
        w, h = Image.open(path).size
    except Exception:
        w, h = 800, 600
    return int(w * h / 750)


def call(tool, args, timeout=60, log=True):
    t0 = time.time()
    res = post("/call", {"tool": tool, "args": args, "timeout": timeout}, timeout=timeout + 30)
    dt = time.time() - t0
    text = res.get("text", "")
    imgs = res.get("images", [])
    tok_in = len(json.dumps(args)) // 4
    tok_out = len(text) // 4 + sum(image_tokens(p) for p in imgs)
    if log:
        os.makedirs(os.path.join(REPO, "test"), exist_ok=True)
        with open(os.path.join(REPO, "test", "calls.jsonl"), "a", encoding="utf-8") as f:
            f.write(json.dumps({"t": time.strftime("%H:%M:%S"), "tool": tool, "args": args, "ok": res.get("ok"),
                                "tok_in": tok_in, "tok_out": tok_out, "sec": round(dt, 2), "text": text, "images": imgs}) + "\n")
    return res, tok_in, tok_out, dt


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    if len(sys.argv) < 2:
        print(__doc__); sys.exit(1)
    tool = sys.argv[1]
    if tool == "health":
        s = server()
        req = urllib.request.Request(f"http://127.0.0.1:{s['port']}/health", headers={"X-Nextra-Token": s["token"]})
        print(urllib.request.urlopen(req, timeout=10).read().decode()); sys.exit(0)
    if tool == "reload":
        print(post("/reload", {})); sys.exit(0)
    args = {}
    timeout = 60
    rest = sys.argv[2:]
    if "--timeout" in rest:
        i = rest.index("--timeout"); timeout = int(rest[i + 1]); del rest[i:i + 2]
    code_file = None
    if "--code" in rest:
        i = rest.index("--code"); code_file = rest[i + 1]; del rest[i:i + 2]
    if rest:
        a = rest[0]
        if a.startswith("@"):
            with open(a[1:], encoding="utf-8") as f: a = f.read()
        args = json.loads(a)
    if code_file:
        with open(code_file, encoding="utf-8") as f: args["code"] = f.read()
    res, ti, to, dt = call(tool, args, timeout)
    print(res.get("text", ""))
    for p in res.get("images", []): print("IMAGE:", p)
    print(f"--- ok={res.get('ok')} {dt:.2f}s ~in {ti} tok / out {to} tok")
