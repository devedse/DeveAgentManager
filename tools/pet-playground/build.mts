// Package the real browser renderers and playground into one offline HTML file.
import fs from 'node:fs';
import path from 'node:path';
import ts from 'typescript';

const root = process.cwd();
const read = (file: string) => fs.readFileSync(path.join(root, file), 'utf8');
const art = 'DeveAgentManager/wwwroot/pets/silkie/';
const assets = { frames: JSON.parse(read(art + 'frames.json')) };
for (const stage of ['chick', 'young', 'adult'])
    assets['silkie-' + stage] = 'data:image/webp;base64,' + fs.readFileSync(path.join(root, art + 'silkie-' + stage + '.webp')).toString('base64');

const modules = ['illustrated-rig', 'ink-egg', 'silkie-art', 'snake-art', 'throw-motion', 'playground'].map(name => {
    let source = read('DeveAgentManager/Browser/pets/' + name + '.ts');
    if (name === 'illustrated-rig') source = source.replace("new URL('../../pets/silkie/' + id + '.webp', import.meta.url).href", 'playgroundAssets[id]');
    if (name === 'silkie-art') source = source.replace('fetch(new URL("../../pets/silkie/frames.json", import.meta.url))', 'Promise.resolve({ ok: true, json: async () => playgroundAssets.frames })');
    if (source.includes('import.meta')) throw new Error('Unbundled asset URL in ' + name);
    const { outputText } = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } });
    return JSON.stringify('./' + name + '.js') + ': (require, exports) => {\n' + outputText + '\n}';
});
const script = `const playgroundAssets = ${JSON.stringify(assets)};
const modules = {${modules.join(',\n')}};
const loaded = {};
function require(id) {
    if (!loaded[id]) { const exports = loaded[id] = {}; modules[id](require, exports); }
    return loaded[id];
}
require('./silkie-art.js').prepare().then(() => require('./playground.js')).catch(error => {
    document.getElementById('pet-status').textContent = 'The playground could not load.';
    console.error(error);
});`;
const portalStyles = read('DeveAgentManager/wwwroot/app.css');
const dark = portalStyles.match(/:root \{[\s\S]*?\n\}/)![0];
const light = portalStyles.match(/:root\[data-theme="light"\] \{[\s\S]*?\n\}/)![0].replace(':root[data-theme="light"]', ':root');
const html = read('DeveAgentManager.Pets/Playground/page.html')
    .replace('/*THEME*/', dark + '\n@media(prefers-color-scheme:light){' + light + '}')
    .replace('/*PLAYGROUND*/', '<script>\n' + script.replaceAll('</script', '<\\/script') + '\n</script>');
const output = path.resolve(process.argv[2] ?? '.validation/pet-playground.html');
fs.mkdirSync(path.dirname(output), { recursive: true });
fs.writeFileSync(output, html);
console.log('Built ' + path.basename(output) + ' (' + (Buffer.byteLength(html) / 1024 / 1024).toFixed(2) + ' MiB)');
