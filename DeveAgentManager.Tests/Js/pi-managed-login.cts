import assert = require('node:assert/strict');
import fs = require('node:fs/promises');
import path = require('node:path');
import os = require('node:os');
import ts = require('typescript');
import { pathToFileURL } from 'node:url';

async function main() {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), 'dam-pi-adapter-js-'));
    try {
        const source = await fs.readFile(process.argv[2], 'utf8');
        const mock = `const builtinProviders = () => [{id:'openai-codex', getModels:()=>[{id:'test-model',provider:'openai-codex'}]}];`;
        const compiled = ts.transpileModule(source.replace(/import \{ builtinProviders \} from [^;]+;/, mock), {
            compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext }
        }).outputText;
        const file = path.join(root, 'adapter.mjs');
        await fs.writeFile(file, compiled);
        const adapter = await import(pathToFileURL(file).href);
        const authFile = path.join(root, 'auth.json');
        const writeToken = (access: string, refresh = '') => fs.writeFile(authFile, JSON.stringify({
            auth_mode: 'chatgptAuthTokens', tokens: { access_token: access, refresh_token: refresh }
        }));
        await writeToken('test-only-access-one');
        process.env.DAM_PI_LOGIN_PROVIDER = 'dam-codex';
        process.env.DAM_PI_CODEX_AUTH_FILE = authFile;
        let provider: any;
        let start: any;
        let selected: any;
        let thinking: string | undefined;
        const pi = {
            registerProvider(value: any) { provider = value; },
            on(event: string, handler: any) { assert.equal(event, 'session_start'); start = handler; },
            async setModel(model: any) { selected = model; return true; },
            setThinkingLevel(value: string) { thinking = value; }
        };
        adapter.default(pi);
        assert.equal(provider.id, 'dam-codex');
        assert.equal(provider.auth.oauth, undefined, 'pi receives no refresh implementation');
        assert.equal(provider.getModels()[0].provider, 'dam-codex');
        assert.equal((await provider.auth.apiKey.resolve()).auth.apiKey, 'test-only-access-one');
        await writeToken('test-only-access-two');
        assert.equal((await provider.auth.apiKey.resolve()).auth.apiKey, 'test-only-access-two', 'each request observes token replacement');
        const before = await fs.readFile(authFile, 'utf8');
        await provider.auth.apiKey.check();
        assert.equal(await fs.readFile(authFile, 'utf8'), before, 'the adapter never writes authentication');
        await writeToken('test-only-access-three', 'test-only-refresh-must-not-be-used');
        await assert.rejects(provider.auth.apiKey.resolve(), /DAM's Codex login is unavailable/);
        await fs.writeFile(authFile, '{truncated test-only-access-secret');
        await assert.rejects(provider.auth.apiKey.resolve(), error => !String(error).includes('test-only-access-secret'));
        await writeToken('test-only-access-four');
        const ctx = { model: {id:'test-model',provider:'github-copilot'}, scopedModels: [],
            modelRegistry: { getAvailable: () => provider.getModels() }, ui: {notify() {}}, shutdown() {throw new Error('unexpected shutdown');} };
        await start({}, ctx);
        assert.equal(selected.provider, 'dam-codex');
        await start({}, {...ctx, scopedModels:[{model:provider.getModels()[0],thinkingLevel:'high'}]});
        assert.equal(thinking, 'high', 'provider binding preserves a scoped thinking suffix');
        selected = undefined;
        await start({}, {...ctx, model: {id:'test-model',provider:'dam-codex'}});
        assert.equal(selected, undefined, 'a resumed model on the bound provider is preserved');
        let stopped = false;
        await start({}, {...ctx, modelRegistry:{getAvailable:()=>[]}, shutdown(){stopped=true;}});
        assert.equal(stopped, true, 'missing login models cannot silently fall back to another account');
        console.log('Passed: renewed access, no refresh/write, redacted failures, provider binding and resume.');
    } finally { await fs.rm(root, {recursive:true,force:true}); }
}
main().catch(error => { console.error(error); process.exitCode=1; });
