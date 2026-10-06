// minimal: does the child survive with stdin open and send a response to one request?
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const exe = process.argv[2];
const cwd = process.argv[3] || undefined;
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dsh-mcp-min-'));
console.log('dir=', dir);
const reqPath = path.join(dir, 'req.jsonl');
const outPath = path.join(dir, 'out.jsonl');
const errPath = path.join(dir, 'err.log');
fs.writeFileSync(reqPath, '');
const child = spawn(exe, [], {
  cwd,
  stdio: [fs.openSync(reqPath, 'r'), fs.openSync(outPath, 'w'), fs.openSync(errPath, 'w')],
});
const reqFd = fs.openSync(reqPath, 'a');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
fs.writeSync(reqFd, JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-03-26', capabilities: {}, clientInfo: { name: 'min', version: '1' } } }) + '\n');
for (let i = 0; i < 30; i++) {
  await sleep(500);
  const out = fs.readFileSync(outPath, 'utf8');
  const err = fs.readFileSync(errPath, 'utf8');
  console.log(`t=${(i + 1) * 500}ms exit=${child.exitCode} out=${out.length}b err=${err.length}b`);
  if (out.includes('"id":1')) { console.log('GOT RESPONSE after', (i + 1) * 500, 'ms'); break; }
  if (child.exitCode !== null) { console.log('EXITED early'); break; }
}
console.log('--- stdout ---');
console.log(fs.readFileSync(outPath, 'utf8').slice(0, 2000));
child.kill();
