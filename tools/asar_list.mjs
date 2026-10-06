import fs from 'node:fs';

const asarPath = process.argv[2];
const filter = process.argv[3] ? new RegExp(process.argv[3], 'i') : null;

const fd = fs.openSync(asarPath, 'r');
const head = Buffer.alloc(16);
fs.readSync(fd, head, 0, 16, 0);
const headerSize = head.readUInt32LE(12);
const headerBuf = Buffer.alloc(headerSize);
fs.readSync(fd, headerBuf, 0, headerSize, 16);
const header = JSON.parse(headerBuf.toString('utf8'));

const out = [];
function walk(node, prefix) {
  for (const [name, entry] of Object.entries(node.files || {})) {
    const p = prefix + '/' + name;
    if (entry.files) walk(entry, p);
    else out.push([p, entry.size || 0, entry.offset]);
  }
}
walk(header, '');
fs.closeSync(fd);

const lines = out
  .filter(([p]) => !filter || filter.test(p))
  .map(([p, s]) => `${p}\t${s}`);
console.log(`total files: ${out.length}, matched: ${lines.length}`);
console.log(lines.slice(0, 2000).join('\n'));
