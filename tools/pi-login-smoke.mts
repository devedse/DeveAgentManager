// Compatibility check against the installed pi CLI. Uses only a dummy access token and sends no prompt.
import assert from 'node:assert/strict';
import {spawn, execFileSync} from 'node:child_process';
import {mkdtemp, writeFile, rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join, resolve} from 'node:path';
import {createInterface} from 'node:readline';

const scratch=await mkdtemp(join(tmpdir(),'dam-pi-login-smoke-'));
const command=process.env.PI_COMMAND??'pi';
const env:NodeJS.ProcessEnv={};
for(const name of ['PATH','HOME','LANG','TERM','TMPDIR','SystemRoot']) if(process.env[name])env[name]=process.env[name];
env.PI_CODING_AGENT_DIR=join(scratch,'pi');
env.DAM_PI_LOGIN_PROVIDER='dam-codex';
env.DAM_PI_CODEX_AUTH_FILE=join(scratch,'codex-auth.json');
await writeFile(env.DAM_PI_CODEX_AUTH_FILE,JSON.stringify({auth_mode:'chatgptAuthTokens',tokens:{access_token:'test-only-dummy-access-token',refresh_token:''}}));
const version=execFileSync(command,['--version'],{env,encoding:'utf8'}).trim();
const child=spawn(command,['--mode','rpc','--extension',resolve('DeveAgentManager.Core/Scripts/pi-managed-login.ts'),
    '--models','dam-codex/*:high','--session-dir',join(scratch,'sessions')],{env,cwd:scratch,stdio:['pipe','pipe','pipe']});
const exited=new Promise<void>(done=>child.once('exit',()=>done()));
let diagnostic='';child.stderr.on('data',data=>diagnostic=(diagnostic+data.toString()).slice(-4000));
const lines=createInterface({input:child.stdout});
let timer:NodeJS.Timeout;
try{
    const state=await new Promise<any>((done,reject)=>{
        timer=setTimeout(()=>reject(new Error('pi did not return its state within 15 seconds. '+diagnostic)),15000);
        child.once('error',reject);
        child.once('exit',()=>reject(new Error('pi exited before returning its state. '+diagnostic)));
        lines.on('line',line=>{try{const message=JSON.parse(line);if(message.type==='response'&&message.id==='dam-smoke')done(message);}catch{}});
        child.stdin.write(JSON.stringify({type:'get_state',id:'dam-smoke'})+'\n');
    });
    assert.equal(state.success,true);
    assert.equal(state.data.model.provider,'dam-codex');
    assert.equal(state.data.isStreaming,false);
    console.log(`Passed: pi ${version} loaded the DAM provider and model scope; no prompt or model request was sent.`);
}finally{
    clearTimeout(timer!);lines.close();child.kill('SIGTERM');
    const kill=setTimeout(()=>child.kill('SIGKILL'),3000);
    await exited;clearTimeout(kill);await rm(scratch,{recursive:true,force:true});
}
