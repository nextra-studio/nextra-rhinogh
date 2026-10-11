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
const VERSION = "0.1.0";

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
    description: "Text answers, cheapest first. rhino: level summary→list(one line + oriented box per object)→detail→verts. gh: summary→list(shorthand)→detail(state,code,data)→data. components: search installed GH components with ports. docs: skill docs. library: saved scripts. status: busy/dialog state (works while Rhino is blocked).",
    inputSchema: { type: "object", properties: {
      scope: { type: "string", enum: ["rhino", "gh", "components", "docs", "library", "status"] },
      filter: { type: "string", description: "rhino: 'r3 r4' 'layer:X type:curve name:C* sel cells:C3:E5 bbox:x0,y0,z0,x1,y1,z1'; gh: 'g12 group:X type:Loft errors sel near:g12:2 cells:..'; components: 'extrude' '@Karamba3D'; docs: 'shorthand'" },
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
      open: { type: "string" }, new: { type: "boolean" }, save: { type: "string" },
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
    description: "User memory. list (prefs, learned rules, library index, recurring mistakes: call at session start) | save_pref {text} | log_mistake {text, fix} | compact (then ask the user, then set_rules {text}) | save_script {name, code, doc} | read_script {name}.",
    inputSchema: { type: "object", properties: {
      action: { type: "string", enum: ["list", "save_pref", "set_prefs", "log_mistake", "compact", "set_rules", "save_script", "read_script", "delete_script"] },
      text: { type: "string" }, fix: { type: "string" }, name: { type: "string" }, code: { type: "string" }, doc: { type: "string" }, lang: { type: "string" },
    } },
  },
].filter(t => MODE !== "ask" || t.name === "view" || t.name === "query");

function serverInfo() {
  const p = path.join(APPDIR, "server.json");
  return JSON.parse(fs.readFileSync(p, "utf8"));
}

function post(urlPath, body, timeoutMs) {
  return new Promise((resolve, reject) => {
    let info;
    try { info = serverInfo(); } catch (e) { return reject(new Error("no server.json")); }
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
  const timeout = Math.min(Math.max(Number(args && args.timeout) || 90, 5), 1800);
  if (args) delete args.timeout;
  let res;
  try { res = await post("/call", { tool: name, args: args || {}, timeout }, (timeout + 20) * 1000); }
  catch (e) { return { content: [{ type: "text", text: NOT_REACHABLE + " (" + e.message + ")" }], isError: true }; }
  const content = [{ type: "text", text: res.text || "" }];
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
        instructions: "Rhino 8 + Grasshopper via 6 tools. Start with memory {action:'list'}, then query/view before editing. Grasshopper edits use shorthand (query {scope:'docs', filter:'shorthand'}). Prefer shorthand > library script > free code.",
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
