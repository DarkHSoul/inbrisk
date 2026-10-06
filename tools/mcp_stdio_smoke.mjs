// MCP stdio smoke test that avoids piped child stdio (sandbox cannot open named pipes):
// the child inherits real file handles, the script drives it over those files.
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const exe = process.argv[2];
const cwd = process.argv[3] || undefined;
const extraArgs = process.argv.slice(4);

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dsh-mcp-smoke-'));
const reqPath = path.join(dir, 'req.jsonl');
const outPath = path.join(dir, 'out.jsonl');
const errPath = path.join(dir, 'err.log');

fs.writeFileSync(reqPath, '');
fs.writeFileSync(outPath, '');
fs.writeFileSync(errPath, '');

const stdinFd = fs.openSync(reqPath, 'r');
const stdoutFd = fs.openSync(outPath, 'w');
const stderrFd = fs.openSync(errPath, 'w');
const reqFd = fs.openSync(reqPath, 'a');

const child = spawn(exe, extraArgs, { cwd, stdio: [stdinFd, stdoutFd, stderrFd] });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const send = (obj) => { fs.writeSync(reqFd, JSON.stringify(obj) + '\n'); };

function readMessages() {
  const raw = fs.readFileSync(outPath, 'utf8');
  return raw.split('\n').filter(Boolean).flatMap((line) => {
    try { return [JSON.parse(line)]; } catch { return []; }
  });
}

async function waitForId(id, ms = 30000) {
  const deadline = Date.now() + ms;
  while (Date.now() < deadline) {
    const hit = readMessages().find((m) => m.id === id);
    if (hit) return hit;
    if (child.exitCode !== null) throw new Error('process exited early, code ' + child.exitCode);
    await sleep(200);
  }
  throw new Error('timeout waiting for id ' + id);
}

try {
  send({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-03-26', capabilities: {}, clientInfo: { name: 'dsh-probe', version: '1.0.0' } } });
  const init = await waitForId(1);
  console.log('INIT ok:', JSON.stringify(init.result?.serverInfo ?? init.error));
  console.log('protocolVersion:', init.result?.protocolVersion);

  send({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} });
  send({ jsonrpc: '2.0', id: 2, method: 'tools/list', params: {} });
  const list = await waitForId(2);
  const tools = list.result?.tools ?? [];
  console.log('TOOLS count:', tools.length);
  console.log('TOOLS:', tools.map((t) => t.name).join(', '));
  child.kill();
  await sleep(300);
  console.log('SMOKE: PASS');
} catch (err) {
  console.error('SMOKE: FAIL —', err.message);
  try { console.error('stderr tail:\n' + fs.readFileSync(errPath, 'utf8').slice(-3000)); } catch {}
  child.kill();
  process.exitCode = 1;
}
