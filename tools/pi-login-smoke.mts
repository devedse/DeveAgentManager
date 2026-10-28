// Compatibility check against the installed pi CLI. Uses only a dummy access token and sends no prompt.
import assert from 'node:assert/strict';
import {spawn, execFileSync} from 'node:child_process';
import {mkdtemp, writeFile, rm, mkdir, symlink} from 'node:fs/promises';
import {tmpdir, homedir} from 'node:os';
import {join, resolve} from 'node:path';
import {createInterface} from 'node:readline';

const scratch=await mkdtemp(join(tmpdir(),'dam-pi-login-smoke-'));
const command=process.env.PI_COMMAND??'pi';
const env:NodeJS.ProcessEnv={};
for(const name of ['PATH','HOME','LANG','TERM','TMPDIR','SystemRoot']) if(process.env[name])env[name]=process.env[name];
env.PI_CODING_AGENT_DIR=join(scratch,'pi');

const root=env.PI_CODING_AGENT_DIR!;
await mkdir(join(root,'git/github.com/earendil-works'),{recursive:true});
await symlink(process.env.PI_CLAUDE_BRIDGE??join(homedir(),'.pi/agent/git/github.com/earendil-works/pi-claude-code'),join(root,'git/github.com/earendil-works/pi-claude-code'));
const logins=[];
for(const [index,harness] of ['codex','codex','claude','claude'].entries()){
    const authFile=join(scratch,`auth-${index}`),command=join(scratch,`claude-${index}.sh`),config=join(scratch,`claude-config-${index}`);
    await writeFile(authFile,harness==='codex'?JSON.stringify({auth_mode:'chatgptAuthTokens',tokens:{access_token:`test-only-dummy-${index}`,refresh_token:''}}):'test-only-cli-env');
    await writeFile(command,'#!/bin/sh\nexit 1\n',{mode:0o700});
    logins.push({provider:`dam-${harness}-${index}`,harness,name:`Test account ${index}`,authFile,command,config});
}
env.DAM_PI_LOGIN_MANIFEST=join(scratch,'logins.json');
await writeFile(env.DAM_PI_LOGIN_MANIFEST,JSON.stringify({logins}));
const version=execFileSync(command,['--version'],{env,encoding:'utf8'}).trim();
async function inspect(sessionFile?:string):Promise<{models:any[];state:any}> {
    const child=spawn(command,['--mode','rpc','--extension',resolve('DeveAgentManager.Core/Scripts/pi-managed-login.ts'),
        '--models','dam-*/*:high','--session-dir',join(scratch,'sessions'),...(sessionFile?['--session',sessionFile]:[])],{env,cwd:scratch,stdio:['pipe','pipe','pipe']});
    const exited=new Promise<void>(done=>child.once('exit',()=>done()));
    let diagnostic='';child.stderr.on('data',data=>diagnostic=(diagnostic+data.toString()).slice(-4000));
    const lines=createInterface({input:child.stdout});
    async function rpc(type:string):Promise<any>{
        return new Promise((done,reject)=>{
            const timer=setTimeout(()=>reject(new Error('pi did not respond within 15 seconds. '+diagnostic)),15000);
            const receive=(line:string)=>{
                let response:any;try{response=JSON.parse(line);}catch{return;}
                if(response.type==='response'&&response.id===type){clearTimeout(timer);lines.off('line',receive);done(response);}
            };
            lines.on('line',receive);
            child.once('error',reject);
            child.stdin.write(JSON.stringify({type,id:type})+'\n');
        });
    }
    try{
        const response=await rpc('get_available_models');
        assert.equal(response.success,true);
        const ids=new Set(response.data.models.map((model:any)=>model.provider));
        for(const login of logins)assert.ok(ids.has(login.provider),`${login.provider} is available`);
        const state=await rpc('get_state');
        assert.equal(state.success,true);assert.equal(state.data.isStreaming,false);
        return {models:response.data.models,state:state.data};
    }finally{
        lines.close();child.kill('SIGTERM');
        const kill=setTimeout(()=>child.kill('SIGKILL'),3000);
        await exited;clearTimeout(kill);
    }
}
try{
    const first=await inspect();
    const model=first.models.find(model=>model.provider==='dam-codex-1');
    assert.ok(model,'second Codex account has a model');
    const sessionFile=join(scratch,'legacy.jsonl'),timestamp=new Date().toISOString();
    const records=[
        {type:'session',version:3,id:'test-only-legacy',timestamp,cwd:scratch},
        {type:'model_change',id:'model',parentId:null,timestamp,provider:'dam-codex',modelId:model.id},
        {type:'message',id:'user',parentId:'model',timestamp,message:{role:'user',content:[{type:'text',text:'Historical test message; do not send.'}],timestamp:Date.now()}},
    ];
    await writeFile(sessionFile,records.map(record=>JSON.stringify(record)).join('\n')+'\n');
    await writeFile(env.DAM_PI_LOGIN_MANIFEST!,JSON.stringify({logins,legacyProvider:'dam-codex-1'}));
    const resumed=await inspect(sessionFile);
    assert.equal(resumed.state.model.provider,'dam-codex-1','legacy preview resumes the same account even after pi initially chooses a fallback');
    console.log(`Passed: pi ${version}, two Claude and two Codex providers, model scope and legacy account resume; no prompt or model request was sent.`);
}finally{await rm(scratch,{recursive:true,force:true});}
