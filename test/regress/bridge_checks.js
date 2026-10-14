// Deterministic bridge checks with fake discovery/HTTP; no Rhino document edits.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { EventEmitter } = require('node:events');
const source = fs.readFileSync(path.join(__dirname, '../../bridge/server/index.js'), 'utf8');

function bridge(mode = 'full') {
  let servers = [{ pid: 1, port: 8765, token: 'test', file: 'first.3dm' }];
  const calls = [];
  let heartbeat;
  const sandbox = {
    Buffer, console,
    process: { env: { APPDATA: 'test-appdata', NEXTRA_MODE: mode }, kill: () => {}, stdin: {}, stdout: { write() {} } },
    setInterval: fn => { heartbeat = fn; return { unref() {} }; },
    require: name => name === 'fs' ? {
      readdirSync: () => servers.map(s => `server-${s.pid}.json`),
      readFileSync: file => JSON.stringify(servers.find(s => file.endsWith(`server-${s.pid}.json`))),
    } : name === 'readline' ? { createInterface: () => ({ on() {} }) }
      : name === 'http' ? { request(options, callback) {
        const request = new EventEmitter();
        request.end = data => {
          calls.push({ path: options.path, body: JSON.parse(data) });
          const response = new EventEmitter(); callback(response);
          response.emit('data', Buffer.from('{"ok":true,"text":"done"}')); response.emit('end');
        };
        return request;
      } } : require(name),
  };
  vm.runInNewContext(source + '\nglobalThis.api = {callTool, serverInfo};', sandbox);
  return { ...sandbox.api, calls, heartbeat, setServers: list => { servers = list; } };
}

(async () => {
  const ask = bridge('ask');
  for (const name of ['run', 'gh_edit', 'undo', 'memory', 'nonexistent']) {
    assert.equal((await ask.callTool(name, {})).isError, true, name);
  }
  assert.equal((await ask.callTool('query', { scope: 'status', dismiss_dialog: true })).isError, true);
  assert.equal(ask.calls.length, 0, 'denied actions must not reach Rhino');
  assert.equal((await ask.callTool('query', { scope: 'rhino' })).isError, false);
  ask.heartbeat();
  assert.equal(ask.calls.at(-1).path, '/health', 'heartbeat must not consume late results');

  const full = bridge();
  assert.equal(full.serverInfo().pid, 1);
  full.setServers([{ pid: 2, port: 8766, file: 'second.3dm' }]);
  assert.throws(() => full.serverInfo(), /bound Rhino/, 'auto-selection must remain bound');
  const rebound = await full.callTool('query', { instance: '2', scope: 'status' });
  assert.equal(rebound.isError, false);
  assert.equal(full.serverInfo().pid, 2);
  full.setServers([{ pid: 2, file: 'second.3dm' }, { pid: 3, file: 'third.3dm' }]);
  assert.equal(full.serverInfo().pid, 2, 'opening a second Rhino must not change binding');

  const restart = bridge();
  assert.equal(restart.serverInfo().pid, 1);
  restart.setServers([{ pid: 7, port: 8767, file: 'first.3dm' }]);
  assert.equal(restart.serverInfo().pid, 7, 'auto-binding follows a Rhino restart with the same file');
  const listed = await restart.callTool('query', { scope: 'instances' });
  assert.match(listed.content[0].text, /bound: pid 7/);
  const chosen = bridge();
  await chosen.callTool('query', { instance: '1', scope: 'status' });
  chosen.setServers([{ pid: 8, port: 8768, file: 'first.3dm' }]);
  assert.throws(() => chosen.serverInfo(), /bound Rhino/, 'an explicitly chosen pid is never replaced');
  console.log('PASS bridge: denied tools/actions, allowed query, heartbeat, sticky binding, rebind, restart follow');
})().catch(error => { console.error(error); process.exitCode = 1; });
