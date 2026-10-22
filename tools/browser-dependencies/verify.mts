import { readFileSync, readdirSync } from 'node:fs';
const sourceRoot = new URL('../../../tools/browser-dependencies/', import.meta.url);
for (const name of ['fit', 'web-links']) {
  const source = readFileSync(new URL(`node_modules/@xterm/addon-${name}/lib/addon-${name}.js`, sourceRoot), 'utf8').split('//# sourceMappingURL')[0].trim();
  const vendor = readFileSync(new URL(`../../DeveAgentManager/wwwroot/lib/xterm/addon-${name}.js`, sourceRoot), 'utf8').trim();
  if (source !== vendor) throw new Error(`Vendored addon-${name} differs from audited npm package`);
}
const pdf = new URL('node_modules/pdfjs-dist/', sourceRoot);
const version = JSON.parse(readFileSync(new URL('package.json', pdf), 'utf8')).version;
const vendor = new URL(`../../DeveAgentManager/wwwroot/lib/pdfjs/${version}/`, sourceRoot);
const paths = ['LICENSE', 'build/pdf.min.mjs', 'build/pdf.worker.min.mjs'];
for (const directory of ['cmaps', 'standard_fonts', 'wasm', 'iccs']) {
  for (const name of readdirSync(new URL(`${directory}/`, pdf))) paths.push(`${directory}/${name}`);
}
for (const path of paths) {
  if (!readFileSync(new URL(path, pdf)).equals(readFileSync(new URL(path, vendor))))
    throw new Error(`Vendored PDF.js ${path} differs from audited npm package`);
}
