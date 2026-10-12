#!/usr/bin/env node
// Nextra MCP bridge: stdio JSON-RPC (MCP) <-> Nextra server inside Rhino (127.0.0.1, token auth).
// Zero dependencies so it runs on the Node that ships with Claude Desktop (.mcpb) or any Node >= 18.
// NEXTRA_MODE=ask exposes only view + query (read-only).
"use strict";
const fs = require("fs");
const path = require("path");
const http = require("http");
const readline = require("readline");

const APPDIR = path.join(process.env.APPDATA || path.join(require("os").homedir(), ".config"), "Nextra");
const MODE = (process.env.NEXTRA_MODE || "full").toLowerCase();
const VERSION = "0.1.1";

const TOOLS = [
  {
    name: "view",
    description: "Image of Rhino or the GH canvas with an 8x8 grid (A-H x 1-8) and short handle tags (r12 / g4). Region-first: crowded overviews return per-cell counts instead of tags; then pass focus:'C3:E5'. Viewport is restored after capture.",
    inputSchema: { type: "object", properties: {
      target: { type: "string", enum: ["rhino", "gh", "gh_preview"], description: "default rhino; gh_preview = unbaked GH geometry in Rhino" },
      focus: { type: "string", description: "cells 'C3:E5' of the last image | handles 'r3 r7' / 'g12' | filter 'layer:Walls' 'group:Inputs'" },
      view: { type: "string", description: "current|top|front|right|left|back|persp|iso|<named view>" },
      mode: { type: "string", description: "display mode, default shaded ('current' keeps)" },
      tags: { type: "string", enum: ["auto", "on", "off"] },
      isolate: { type: "boolean", description: "hide everything except focus" },
      size: { type: "array", items: { type: "number" }, description: "[w,h] px, default 800x600" },
    } },
  },
  {
    name: "query",
    description: "Text answers, cheapest first. rhino: level summary→list(one line + oriented box per object)→detail→verts. gh: summary→list(shorthand)→detail(state,code,data)→data. components: search installed GH components with ports. api: search RhinoCommon / GH SDK / rhinoscriptsyntax reference. docs: skill docs + user's workflows. library: saved scripts. status: busy/dialog state (works while Rhino is blocked).",
    inputSchema: { type: "object", properties: {
      scope: { type: "string", enum: ["rhino", "gh", "components", "api", "docs", "library", "status", "instances"] },
      instance: { type: "string", description: "bind this session to the Rhino that has this file open (or pid); also accepted by every tool; sticky" },
      filter: { type: "string", description: "rhino: 'r3 r4' 'layer:X type:curve name:C* sel cells:C3:E5 bbox:x0,y0,z0,x1,y1,z1'; gh: 'g12 group:X type:Loft errors sel near:g12:2 cells:..'; components: 'extrude' '@Karamba3D'; api: 'loft curves' 'Brep.CreateFromLoft' 'rs.AddLoftSrf'; docs: 'shorthand' 'workflows/x'" },
      level: { type: "string", enum: ["summary", "list", "detail", "verts", "data"] },
      limit: { type: "integer" }, offset: { type: "integer" },
      name: { type: "string", description: "library: script name to read" },
      dismiss_dialog: { type: "boolean", description: "status only: cancel the modal dialog blocking Rhino" },
    } },
  },
  {
    name: "run",
    description: "Run Python 3 (default) or C# in Rhino. Helpers: doc, rg, h('r3'|'g5') → object, handle(obj), ghdoc(), args. Returns stdout, Rhino delta (+added ~modified -deleted with handles), GH delta, errors with the failing lines, checkpoint id. Fix with patch instead of resending code.",
    inputSchema: { type: "object", properties: {
      lang: { type: "string", enum: ["py", "cs"] },
      code: { type: "string" },
      patch: { type: "array", items: { type: "object", properties: { find: { type: "string" }, replace: { type: "string" } }, required: ["find", "replace"] }, description: "edits applied to the previous code of this lang" },
      file: { type: "string" }, lib: { type: "string", description: "library script name" },
      args: { type: "object" }, save_as: { type: "string", description: "save to library on success" },
      label: { type: "string" },
    } },
  },
  {
    name: "gh_edit",
    description: "Edit the Grasshopper canvas with shorthand diffs (grammar: query scope:'docs' filter:'shorthand'). Lines apply independently; failures come back per line with valid ports/alternatives. Also: script edits, open/new/save, bake, layout:'tidy'. Auto layout + checkpoint + solve; returns ids, errors, results.",
    inputSchema: { type: "object", properties: {
      ops: { type: "string", description: "shorthand lines, e.g. 'n = Number Slider {v=5, r=1..10, int}\\nc = Circle (R=n)'" },
      script: { type: "object", description: "{handle, code | patch:[{find,replace}] | file | mirror:true | from_mirror:true}" },
      layout: { type: "string", description: "auto (default) | tidy | tidy:<filter> | none" },
      open: { type: "string" }, new: { type: "boolean" }, save: { type: "string", description: ".gh/.ghx, or .ghs = the definition as shorthand text (readable, diffable; open builds it)" },
      bake: { type: "array", items: { type: "string" }, description: "handles (g5 or g5.Port) to bake" }, layer: { type: "string" },
      solve: { type: "boolean" }, atomic: { type: "boolean" }, echo: { type: "boolean" }, show: { type: "boolean" },
    } },
  },
  {
    name: "undo",
    description: "Revert to before a checkpoint (from run / gh_edit replies) or the last N steps. Rhino objects are restored exactly; GH document from its archive.",
    inputSchema: { type: "object", properties: { checkpoint: { type: "string" }, steps: { type: "integer" }, list: { type: "boolean" } } },
  },
  {
    name: "memory",
    description: "User memory. list (user's preferences + workflows, learned rules, library, recurring mistakes: call at session start) | save_pref {text:'key: value'} (also unasked, whenever the user states a lasting preference; same key replaces) | log_mistake {text, fix} | compact (then ask the user, then set_rules {text}) | save_script {name, code, doc} | read_script {name} | save_workflow {name, when, text} / save_guide {name=plug-in category, text}: only when the user asks (save_guide = when the skill folder is read-only; otherwise write plugins/<Category>.md in the skill).",
    inputSchema: { type: "object", properties: {
      action: { type: "string", enum: ["list", "save_pref", "set_prefs", "log_mistake", "compact", "set_rules", "save_script", "read_script", "delete_script", "save_workflow", "save_guide"] },
      text: { type: "string" }, fix: { type: "string" }, name: { type: "string" }, code: { type: "string" }, doc: { type: "string" }, lang: { type: "string" }, when: { type: "string" },
    } },
  },
].filter(t => MODE !== "ask" || t.name === "view" || t.name === "query");

// ---- instance binding (no cross-talk between Rhino instances) ----
// Every Rhino publishes server-<pid>.json {port, token, pid, started, file, path}. A session is bound to ONE instance:
// NEXTRA_FILE="house.3dm" (or NEXTRA_PID) in the MCP config, or `instance:"house.3dm"` on any call (sticky afterwards).
// Several instances and no binding → refuse and list them; bound instance gone → error, never another Rhino.
function alive(pid) { try { process.kill(pid, 0); return true; } catch (e) { return e.code === "EPERM"; } }
function instances() {
  const out = [];
  try {
    for (const f of fs.readdirSync(APPDIR)) {
      if (!/^server-\d+\.json$/.test(f)) continue;
      try { const c = JSON.parse(fs.readFileSync(path.join(APPDIR, f), "utf8")); if (alive(c.pid)) out.push(c); } catch {}
    }
  } catch {}
  return out.sort((a, b) => String(b.started).localeCompare(String(a.started)));
}
let BOUND = process.env.NEXTRA_PID ? { pid: process.env.NEXTRA_PID } : process.env.NEXTRA_FILE ? { file: process.env.NEXTRA_FILE } : null;
const fileMatch = (c, f) => { f = String(f).toLowerCase(); return String(c.file || "").toLowerCase() === f || String(c.file || "").toLowerCase().includes(f) || String(c.path || "").toLowerCase().includes(f); };
const describe = list => list.map(c => `pid ${c.pid}: ${c.file || "untitled"}${c.path ? " (" + c.path + ")" : ""}`).join("; ");
function serverInfo() {
  const all = instances();
  if (all.length === 0) throw new Error("no running Rhino with Nextra");
  if (BOUND) {
    const hit = all.filter(c => BOUND.pid ? String(c.pid) === String(BOUND.pid) : fileMatch(c, BOUND.file));
    if (hit.length === 1) { BOUND = { pid: hit[0].pid, file: hit[0].file }; return hit[0]; }
    if (hit.length > 1) throw new Error(`"${BOUND.file}" matches several Rhino instances (${describe(hit)}); pass instance:"<pid>"`);
    throw new Error(`bound Rhino (${BOUND.file || "pid " + BOUND.pid}) is not running. Open instances: ${describe(all)}. Pass instance:"<file or pid>" to rebind.`);
  }
  if (all.length === 1) return all[0];
  throw new Error(`${all.length} Rhino instances are open — choose one with instance:"<file name or pid>" (sticky for this session): ${describe(all)}`);
}

function post(urlPath, body, timeoutMs) {
  return new Promise((resolve, reject) => {
    let info;
    try { info = serverInfo(); } catch (e) { return reject(e); }
    const data = Buffer.from(JSON.stringify(body));
    const req = http.request({ host: "127.0.0.1", port: info.port, path: urlPath, method: "POST", timeout: timeoutMs,
      headers: { "Content-Type": "application/json", "Content-Length": data.length, "X-Nextra-Token": info.token } }, res => {
      const chunks = [];
      res.on("data", c => chunks.push(c));
      res.on("end", () => { try { resolve(JSON.parse(Buffer.concat(chunks).toString("utf8"))); } catch (e) { reject(e); } });
    });
    req.on("timeout", () => req.destroy(new Error("http timeout")));
    req.on("error", reject);
    req.end(data);
  });
}

const NOT_REACHABLE = "Rhino is not reachable. Ask the user to start Rhino 8 with the Nextra plug-in (a green/amber dot sits at the bottom-right of the Rhino window; red = click it to restart; command NextraOn if it is off). Do not retry in a loop.";

async function callTool(name, args) {
  args = args || {};
  if (args.instance !== undefined) {           // (re)bind this session to one Rhino instance
    const v = String(args.instance); delete args.instance;
    BOUND = /^\d+$/.test(v) ? { pid: v } : { file: v };
  }
  if (name === "query" && args.scope === "instances") {
    const all = instances();
    let cur = null; try { cur = serverInfo(); } catch {}
    return { content: [{ type: "text", text: (all.length ? describe(all) : "no Rhino with Nextra running") + (cur ? `\nbound: pid ${cur.pid} ${cur.file}` : "\nnot bound") }] };
  }
  const timeout = Math.min(Math.max(Number(args && args.timeout) || 90, 5), 1800);
  if (args) delete args.timeout;
  let res;
  try { res = await post("/call", { tool: name, args: args || {}, timeout }, (timeout + 20) * 1000); }
  catch (e) {
    const msg = /instances|bound|matches/.test(e.message) ? e.message : NOT_REACHABLE + " (" + e.message + ")";
    return { content: [{ type: "text", text: msg }], isError: true };
  }
  let info = null; try { info = serverInfo(); } catch {}
  const content = [{ type: "text", text: (res.text || "") }];
  for (const img of res.images || []) {
    try { content.push({ type: "image", data: fs.readFileSync(img).toString("base64"), mimeType: "image/png" }); }
    catch (e) { content.push({ type: "text", text: "(image unreadable: " + img + ")" }); }
  }
  return { content, isError: res.ok === false };
}

// heartbeat: keeps the status dot green while this bridge is alive
setInterval(() => { post("/call", { tool: "query", args: { scope: "status" }, timeout: 5 }, 8000).catch(() => {}); }, 45000).unref();

function send(msg) { process.stdout.write(JSON.stringify(msg) + "\n"); }

const rl = readline.createInterface({ input: process.stdin });
rl.on("line", async line => {
  if (!line.trim()) return;
  let msg;
  try { msg = JSON.parse(line); } catch { return; }
  const { id, method, params } = msg;
  try {
    if (method === "initialize") {
      send({ jsonrpc: "2.0", id, result: {
        protocolVersion: (params && params.protocolVersion) || "2025-06-18",
        capabilities: { tools: {} },
        serverInfo: { name: "nextra-rhinogh", version: VERSION },
        instructions: "Several Rhino windows? query {scope:'instances'} then pass instance:'file.3dm' once (sticky); the bridge never talks to an unbound instance. Rhino 8 + Grasshopper via 6 tools. Start with memory {action:'list'}, then query/view before editing. Grasshopper edits use shorthand (query {scope:'docs', filter:'shorthand'}). Prefer shorthand > library script > free code.",
      } });
    } else if (method === "tools/list") {
      send({ jsonrpc: "2.0", id, result: { tools: TOOLS } });
    } else if (method === "tools/call") {
      const result = await callTool(params.name, params.arguments || {});
      send({ jsonrpc: "2.0", id, result });
    } else if (method === "ping") {
      send({ jsonrpc: "2.0", id, result: {} });
    } else if (id !== undefined) {
      send({ jsonrpc: "2.0", id, error: { code: -32601, message: "method not found: " + method } });
    }
  } catch (e) {
    if (id !== undefined) send({ jsonrpc: "2.0", id, error: { code: -32603, message: String(e && e.message || e) } });
  }
});
