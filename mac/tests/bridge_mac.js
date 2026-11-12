// End-to-end check of the real bridge (bridge/server/index.js) as it runs on a Mac:
// a fake Rhino host publishes server-<pid>.json where the Mac plug-in writes it, the bridge is started as a child
// process with HOME = a temp folder and no APPDATA, and we talk MCP to it over stdio.
//   node mac/tests/bridge_mac.js            (on macOS: real platform; elsewhere: process.platform is emulated as darwin)
// Also checks that with APPDATA set (Windows) the bridge still uses %APPDATA%\NextraRhinoGh, i.e. Windows is unchanged.
"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const http = require("node:http");
const { spawn } = require("node:child_process");

const repo = path.join(__dirname, "..", "..");
const BRIDGES = [path.join(repo, "bridge", "server", "index.js"), path.join(repo, "plugin", "nextra-rhinogh", "server", "index.js")];

// what the Mac host (Server.AppDir under NEXTRA_MAC) computes: UserProfile/Library/Application Support/NextraRhinoGh
const hostAppDir = home => path.join(home, "Library", "Application Support", "NextraRhinoGh");

function fakeHost(token) {
  const calls = [];
  const server = http.createServer((req, res) => {
    let body = "";
    req.on("data", c => body += c);
    req.on("end", () => {
      calls.push({ path: req.url, token: req.headers["x-nextra-token"], body: body ? JSON.parse(body) : null });
      res.setHeader("Content-Type", "application/json");
      if (req.headers["x-nextra-token"] !== token) { res.statusCode = 403; return res.end('{"ok":false,"text":"bad token"}'); }
      const b = body ? JSON.parse(body) : {};
      res.end(JSON.stringify({ ok: true, text: `fake rhino got ${b.tool || req.url}`, images: [] }));
    });
  });
  return new Promise(r => server.listen(0, "127.0.0.1", () => r({ server, port: server.address().port, calls })));
}

function startBridge(script, env, emulateDarwin) {
  const args = [];
  if (emulateDarwin) args.push("--require", path.join(__dirname, "as_darwin.js"));
  args.push(script);
  const child = spawn(process.execPath, args, { env, stdio: ["pipe", "pipe", "inherit"] });
  let buf = "";
  const waiting = new Map();
  child.stdout.on("data", d => {
    buf += d;
    let i;
    while ((i = buf.indexOf("\n")) >= 0) {
      const line = buf.slice(0, i); buf = buf.slice(i + 1);
      if (!line.trim()) continue;
      const msg = JSON.parse(line);
      waiting.get(msg.id)?.(msg); waiting.delete(msg.id);
    }
  });
  let next = 1;
  const rpc = (method, params) => new Promise((resolve, reject) => {
    const id = next++;
    const t = setTimeout(() => reject(new Error("bridge did not answer " + method)), 10000);
    waiting.set(id, m => { clearTimeout(t); resolve(m); });
    child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
  });
  return { child, rpc };
}

function cleanEnv(extra) {
  const env = { ...process.env, ...extra };
  for (const k of ["APPDATA", "NEXTRA_PID", "NEXTRA_FILE", "XDG_CONFIG_HOME"]) if (!(k in extra)) delete env[k];
  return env;
}

async function macCase(script) {
  const emulate = process.platform !== "darwin";
  const home = fs.mkdtempSync(path.join(os.tmpdir(), "nx-mac-home-"));
  const token = "t" + Math.random().toString(16).slice(2);
  const host = await fakeHost(token);
  const dir = hostAppDir(home);
  fs.mkdirSync(dir, { recursive: true });
  // a live pid (this test process), like the real host's server-<pid>.json
  fs.writeFileSync(path.join(dir, `server-${process.pid}.json`), JSON.stringify(
    { port: host.port, token, pid: process.pid, started: new Date().toISOString(), file: "house.3dm", path: "/Users/me/house.3dm" }));
  // decoy where the OLD bridge logic looked on a Mac: must be ignored
  fs.mkdirSync(path.join(home, ".config", "NextraRhinoGh"), { recursive: true });

  const b = startBridge(script, cleanEnv({ HOME: home, USERPROFILE: home }), emulate);
  try {
    const init = await b.rpc("initialize", { protocolVersion: "2025-06-18" });
    assert.equal(init.result.serverInfo.name, "nextra-rhinogh");
    const inst = await b.rpc("tools/call", { name: "query", arguments: { scope: "instances" } });
    const text = inst.result.content[0].text;
    assert.match(text, new RegExp(`pid ${process.pid}: house\\.3dm`), "instance not found: " + text);
    assert.match(text, /bound: pid/);
    const call = await b.rpc("tools/call", { name: "query", arguments: { scope: "scene" } });
    assert.equal(call.result.isError, false, JSON.stringify(call.result));
    assert.equal(call.result.content[0].text, "fake rhino got query");
    const hit = host.calls.find(c => c.path === "/call");
    assert.ok(hit, "bridge never reached the fake Rhino");
    assert.equal(hit.token, token);
    assert.deepEqual(hit.body.args, { scope: "scene" });
    console.log(`PASS mac  ${path.relative(repo, script)}: found ${dir.replace(home, "~")}/server-<pid>.json, MCP call reached the host${emulate ? " (darwin emulated)" : ""}`);
  } finally { b.child.kill(); host.server.close(); fs.rmSync(home, { recursive: true, force: true }); }
}

async function windowsCase(script) {
  // APPDATA set → %APPDATA%\NextraRhinoGh regardless of platform: the Windows path is unchanged by the Mac branch
  const appdata = fs.mkdtempSync(path.join(os.tmpdir(), "nx-win-appdata-"));
  const token = "w" + Math.random().toString(16).slice(2);
  const host = await fakeHost(token);
  fs.mkdirSync(path.join(appdata, "NextraRhinoGh"), { recursive: true });
  fs.writeFileSync(path.join(appdata, "NextraRhinoGh", `server-${process.pid}.json`), JSON.stringify(
    { port: host.port, token, pid: process.pid, started: new Date().toISOString(), file: "win.3dm" }));
  const b = startBridge(script, cleanEnv({ APPDATA: appdata }), true);   // even when "darwin", APPDATA wins
  try {
    await b.rpc("initialize", {});
    const inst = await b.rpc("tools/call", { name: "query", arguments: { scope: "instances" } });
    assert.match(inst.result.content[0].text, /win\.3dm/);
    console.log(`PASS win  ${path.relative(repo, script)}: APPDATA set → %APPDATA%/NextraRhinoGh (unchanged)`);
  } finally { b.child.kill(); host.server.close(); fs.rmSync(appdata, { recursive: true, force: true }); }
}

(async () => {
  console.log(`node ${process.version} on ${process.platform}`);
  for (const s of BRIDGES) { await macCase(s); await windowsCase(s); }
  console.log("bridge_mac: all passed");
})().catch(e => { console.error("FAIL", e.message); process.exit(1); });
