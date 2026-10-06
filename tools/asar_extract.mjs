import fs from 'node:fs';
import path from 'node:path';

const asarPath = process.argv[2];
const outRoot = process.argv[3];
const want = process.argv.slice(4);

const fd = fs.openSync(asarPath, 'r');
const head = Buffer.alloc(16);
fs.readSync(fd, head, 0, 16, 0);
const headerSize = head.readUInt32LE(12);
const headerBuf = Buffer.alloc(headerSize);
fs.readSync(fd, headerBuf, 0, headerSize, 16);
const header = JSON.parse(headerBuf.toString('utf8'));
const dataOffset = 16 + headerSize;

function find(node, prefix, target) {
  for (const [name, entry] of Object.entries(node.files || {})) {
    const p = prefix + '/' + name;
    if (entry.files) {
      const r = find(entry, p, target);
      if (r) return r;
    } else if (p === target) return [p, entry];
  }
  return null;
}

for (const w of want) {
  const target = w.startsWith('/') ? w : '/' + w;
  const hit = find(header, '', target);
  if (!hit) { console.log('MISSING ' + target); continue; }
  const [, entry] = hit;
  const buf = Buffer.alloc(entry.size);
  fs.readSync(fd, buf, 0, entry.size, dataOffset + Number(entry.offset));
  const dest = path.join(outRoot, w.replace(/[:/\\]+/g, '_'));
  fs.mkdirSync(outRoot, { recursive: true });
  fs.writeFileSync(dest, buf);
  console.log('OK ' + dest + ' (' + entry.size + ' bytes)');
}
fs.closeSync(fd);
