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
        await adapter.default(pi);
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
        const piRoot = path.join(root, 'pi');
        const moduleFolder = path.join(piRoot, 'git/github.com/earendil-works/pi-claude-code/src');
        await fs.mkdir(moduleFolder, {recursive:true});
        await fs.writeFile(path.join(moduleFolder, 'provider.ts'), `
            export function createClaudeCodeProvider() {
                return {id:'claude-code',getModels:()=>[{id:'default',provider:'claude-code'}],
                    refreshModels:()=>{throw Error('must not use shared discovery state');},
                    stream:(model,ctx,options)=>options,streamSimple:(model,ctx,options)=>options};
            }
        `);
        process.env.PI_CODING_AGENT_DIR = piRoot;
        const secondAuth = path.join(root, 'second-auth.json');
        await fs.writeFile(secondAuth, JSON.stringify({auth_mode:'chatgptAuthTokens',tokens:{access_token:'test-only-second-account'}}));
        const claudeAuth = path.join(root, 'claude-env');
        await fs.writeFile(claudeAuth, 'test-only-cli-environment');
        const logins = [
            {provider:'dam-codex-one',harness:'codex',name:'First Codex',authFile,command:'',config:''},
            {provider:'dam-codex-two',harness:'codex',name:'Second Codex',authFile:secondAuth,command:'',config:''},
            {provider:'dam-claude-one',harness:'claude',name:'First Claude',authFile:claudeAuth,command:'/first-wrapper',config:'/first-config'},
            {provider:'dam-claude-two',harness:'claude',name:'Second Claude',authFile:claudeAuth,command:'/second-wrapper',config:'/second-config'},
        ];
        process.env.DAM_PI_LOGIN_MANIFEST = path.join(root, 'manifest.json');
        await fs.writeFile(process.env.DAM_PI_LOGIN_MANIFEST, JSON.stringify({logins,legacyProvider:'dam-codex-two'}));
        const providers:any[]=[];
        await adapter.default({...pi,registerProvider(value:any){providers.push(value);}});
        assert.deepEqual(providers.map(p=>p.id),logins.map(l=>l.provider));
        assert.deepEqual(providers.map(p=>p.getModels()[0].provider),logins.map(l=>l.provider));
        assert.equal((await providers[0].auth.apiKey.resolve()).auth.apiKey,'test-only-access-four');
        assert.equal((await providers[1].auth.apiKey.resolve()).auth.apiKey,'test-only-second-account');
        await writeToken('test-only-renewed-first');
        assert.equal((await providers[0].auth.apiKey.resolve()).auth.apiKey,'test-only-renewed-first');
        assert.equal((await providers[1].auth.apiKey.resolve()).auth.apiKey,'test-only-second-account');
        const beforeEnv = {...process.env};
        const auth = await Promise.all(providers.slice(2).map(p=>p.auth.apiKey.resolve()));
        assert.equal(auth[0].env.PI_CLAUDE_CODE_COMMAND,'/first-wrapper');
        assert.equal(auth[1].env.PI_CLAUDE_CODE_COMMAND,'/second-wrapper');
        for (let i=2;i<4;i++) {
            assert.equal(providers[i].auth.oauth,undefined);
            assert.equal(providers[i].refreshModels,undefined);
            for(const method of ['stream','streamSimple']) {
                const options=providers[i][method](providers[i].getModels()[0],{}, {env:{PI_CLAUDE_CODE_COMMAND:'/wrong-wrapper',ANTHROPIC_API_KEY:'test-only-wrong-key'}});
                assert.equal(options.env.PI_CLAUDE_CODE_COMMAND,i===2?'/first-wrapper':'/second-wrapper');
                assert.equal(options.env.ANTHROPIC_API_KEY,'');
                assert.equal(options.env.CLAUDE_CODE_OAUTH_TOKEN,'');
            }
        }
        assert.ok(JSON.stringify(process.env)===JSON.stringify(beforeEnv),'provider operations never mutate shared environment');
        selected=undefined;
        await start({}, {...ctx,model:{id:'test-model',provider:'dam-codex'},modelRegistry:{getAvailable:()=>providers.flatMap(p=>p.getModels())}});
        assert.equal(selected.provider,'dam-codex-two','legacy resume maps only to the same explicit account');
        selected=undefined;
        await start({}, {...ctx,model:{id:'default',provider:'dam-claude-two'}});
        assert.equal(selected,undefined,'a selected account and model are preserved on resume');
        await fs.rm(secondAuth);
        await assert.rejects(providers[1].auth.apiKey.resolve(), /DAM's Codex login is unavailable/);
        assert.equal((await providers[0].auth.apiKey.resolve()).auth.apiKey,'test-only-renewed-first');
        console.log('Passed: four distinct providers, renewal, no refresh/write, isolated Claude streams, legacy resume and revoked files.');
    } finally { await fs.rm(root, {recursive:true,force:true}); }
}
main().catch(error => { console.error(error); process.exitCode=1; });
