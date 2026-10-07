import { readFileSync } from 'node:fs';
for (const name of ['fit', 'web-links']) {
  const source = readFileSync(new URL(`node_modules/@xterm/addon-${name}/lib/addon-${name}.js`, import.meta.url), 'utf8').split('//# sourceMappingURL')[0].trim();
  const vendor = readFileSync(new URL(`../../DeveAgentManager/wwwroot/lib/xterm/addon-${name}.js`, import.meta.url), 'utf8').trim();
  if (source !== vendor) throw new Error(`Vendored addon-${name} differs from audited npm package`);
}
