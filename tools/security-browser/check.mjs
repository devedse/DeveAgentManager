import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { spawn, execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
const scratch = mkdtempSync(join(tmpdir(), 'dam-browser-security-'));
const appPort = 19080, proxyPort = 19443;
const portal = `https://dam.test:${proxyPort}`;
const editor = `https://alpha.code.dam.test:${proxyPort}`;
const password = 'isolated-test-password-1234';
let manager, proxy, browser;
const logs = [], errors = [];
try {
  execFileSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', join(scratch,'key.pem'), '-out', join(scratch,'cert.pem'), '-days', '1', '-subj', '/CN=dam.test'], {stdio:'ignore'});
  manager = spawn(process.env.DOTNET || 'dotnet', [resolve(process.env.DAM_DLL || '.validation/publish/DeveAgentManager.dll')], {
    cwd: resolve('.validation/publish'), env: {...process.env, DATA_DIR: join(scratch,'data'), ADMIN_PASSWORD: password,
      PROXMOX_URL:'', ASPNETCORE_ENVIRONMENT:'Production', ASPNETCORE_URLS:`http://127.0.0.1:${appPort}`,
      PUBLIC_ORIGINS:portal, EDITOR_URL:`https://{runner}.code.{host}:${proxyPort}`, TRUSTED_PROXIES:'127.0.0.1', ALLOW_LOCAL_ACCESS:'false'}
  });
  manager.stdout.on('data', b=>logs.push(b.toString())); manager.stderr.on('data', b=>logs.push(b.toString()));
  proxy = https.createServer({key:readFileSync(join(scratch,'key.pem')), cert:readFileSync(join(scratch,'cert.pem'))}, (req,res)=> {
    if (req.headers.host?.startsWith('alpha.code.') && req.url === '/hostile-fixture') {
      res.setHeader('Content-Type','text/html'); res.end('<!doctype html><title>Hostile editor fixture</title><body>Compromised Runner</body>'); return;
    }
    const headers={...req.headers,'x-forwarded-proto':'https','x-forwarded-for':'198.51.100.10'};
    const upstream=http.request({host:'127.0.0.1',port:appPort,path:req.url,method:req.method,headers}, r=> {res.writeHead(r.statusCode,r.headers);r.pipe(res);});
    upstream.on('error',()=>{res.writeHead(502);res.end();});req.pipe(upstream);
  });
  proxy.on('upgrade',(req,socket,head)=> {
    const upstream=net.connect(appPort,'127.0.0.1',()=> {
      upstream.write(`${req.method} ${req.url} HTTP/1.1\r\n`+Object.entries({...req.headers,'x-forwarded-proto':'https','x-forwarded-for':'198.51.100.10'}).map(([k,v])=>`${k}: ${v}`).join('\r\n')+'\r\n\r\n');
      if(head.length)upstream.write(head);socket.pipe(upstream);upstream.pipe(socket);
    });upstream.on('error',()=>socket.destroy());socket.on('error',()=>upstream.destroy());
  });
  await new Promise(r=>proxy.listen(proxyPort,'127.0.0.1',r));
  for(let i=0;i<100;i++) {
    const ready=await new Promise(r=> {const req=http.get({host:'127.0.0.1',port:appPort,path:'/health',headers:{host:`dam.test:${proxyPort}`,'x-forwarded-proto':'https'}},res=>{res.resume();r(res.statusCode===200);});req.on('error',()=>r(false));});
    if(ready)break;if(i===99)throw new Error('Isolated Manager did not start: '+logs.join('').slice(-2000));await new Promise(r=>setTimeout(r,100));
  }
  browser=await chromium.launch({headless:true,args:['--no-proxy-server','--host-resolver-rules=MAP dam.test 127.0.0.1, MAP *.code.dam.test 127.0.0.1']});
  const context=await browser.newContext({ignoreHTTPSErrors:true});
  const page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));
  page.on('console',m=> {if(m.type()==='error' && /Content Security Policy|Refused to|EvalError/.test(m.text()))errors.push(m.text());});
  await page.goto(portal+'/login');await page.locator('[name=username]').fill('admin');await page.locator('[name=password]').fill(password);
  await Promise.all([page.waitForURL(portal+'/'),page.locator('button[type=submit]').click()]);
  await page.getByText('Dashboard',{exact:true}).first().waitFor({timeout:30000});
  assert.equal(await page.evaluate(async()=> (await fetch('/api/me')).status),200);
  await page.goto(portal+'/security');await page.getByRole('heading',{name:'Account security',exact:true}).waitFor();
  assert.equal(await page.locator('input[name=__RequestVerificationToken]').count()>0,true);
  await Promise.all([page.waitForURL(portal+'/security'), page.getByRole('button',{name:'Enroll authenticator',exact:true}).click()]);
  const secret=await page.locator('code').innerText();
  const alphabet='ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';let buffer=0,bits=0;const bytes=[];
  for(const char of secret){buffer=(buffer<<5)|alphabet.indexOf(char);bits+=5;if(bits>=8){bits-=8;bytes.push((buffer>>bits)&255);}}
  const counter=Buffer.alloc(8);counter.writeBigInt64BE(BigInt(Math.floor(Date.now()/30000)));
  const digest=createHmac('sha1',Buffer.from(bytes)).update(counter).digest();const offset=digest[digest.length-1]&15;
  const code=String((digest.readUInt32BE(offset)&0x7fffffff)%1000000).padStart(6,'0');
  await page.locator('form[action="/account/security/confirm"] input[name=code]').fill(code);
  await Promise.all([page.waitForURL(portal+'/account/security/confirm'),page.getByRole('button',{name:'Enable and show recovery codes',exact:true}).click()]);
  const recovery=(await page.locator('pre').innerText()).trim().split('\n');assert.equal(recovery.length,10);
  await page.getByRole('link',{name:'Back to account security',exact:true}).click();
  await page.locator('form[action="/account/security/reauth"] input[name=password]').fill(password);
  await page.locator('form[action="/account/security/reauth"] input[name=code]').fill(recovery[0]);
  await Promise.all([page.waitForURL(portal+'/security'),page.getByRole('button',{name:'Confirm identity',exact:true}).click()]);
  const auth=(await context.cookies(portal)).find(c=>c.name==='__Host-DAM.Auth');assert(auth?.secure && auth.httpOnly && auth.path==='/');
  const hostile=await context.newPage();await hostile.goto(editor+'/hostile-fixture');
  const outcomes=await hostile.evaluate(async({portal})=> {
    let read=false;try {await (await fetch(portal+'/api/me',{credentials:'include'})).json();read=true;}catch{}
    const logout=await fetch(portal+'/account/logout',{method:'POST',credentials:'include',body:new URLSearchParams()}).then(r=>r.status).catch(()=>-1);
    document.cookie='__Host-DAM.Auth=attacker; Domain=dam.test; Path=/; Secure';
    return {read,logout};
  },{portal});assert.equal(outcomes.read,false);assert.notEqual(outcomes.logout,200);
  assert.equal((await context.cookies(portal)).find(c=>c.name==='__Host-DAM.Auth')?.value,auth.value);
  assert.equal(await page.evaluate(async()=> (await fetch('/api/me')).status),200);
  await page.goto(portal+'/logout');await Promise.all([page.waitForURL(portal+'/login'),page.getByRole('button',{name:'Sign out',exact:true}).click()]);
  assert.equal(await page.evaluate(async()=> (await fetch('/api/me')).status),401);
  await page.locator('[name=username]').fill('admin');await page.locator('[name=password]').fill(password);await page.locator('[name=code]').fill(recovery[1]);
  await Promise.all([page.waitForURL(portal+'/'),page.locator('button[type=submit]').click()]);
  assert.equal(await page.evaluate(async()=> (await fetch('/api/me')).status),200);
  assert.deepEqual(errors,[]);
  console.log('PASS: production Portal boots under CSP, forms work, public cookie hardened, hostile editor cannot read Portal or log it out or plant auth cookie, logout ends API access; TOTP enrollment, recovery display, recent authentication and recovery-code sign-in work.');
} finally {
  await browser?.close();proxy?.closeAllConnections();await new Promise(r=>proxy ? proxy.close(r) : r());
  if(manager){manager.kill('SIGTERM');await new Promise(r=>manager.once('exit',r));}
  rmSync(scratch,{recursive:true,force:true});
}
