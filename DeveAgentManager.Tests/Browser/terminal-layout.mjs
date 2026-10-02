const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';
const browser = await chromium.launch();
try {
 const context = await browser.newContext({viewport:{width:1100,height:900},hasTouch:true,deviceScaleFactor:1.5});
 const page = await context.newPage();
 const base=process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
 await context.request.post(base+'/account/login',{form:{username:'admin',password:process.env.DAM_TEST_PASSWORD ?? 'preview-test-only',returnUrl:'/'}});
 const info=await (await context.request.get(base+'/api/info')).json();
 const id='00000000-0000-0000-0000-000000000001',now=new Date().toISOString();
 const agent={id,runnerId:id,runnerName:'runner-01',runnerState:'Ready',runnerHarnessIds:['codex','claude'],runnerToolIds:['vscode'],name:'deveagentmanager',harnessId:'codex',workingDirectory:'/work/deveagentmanager',state:'Running',accountName:'Personal',createdAt:now,updatedAt:now,activity:{status:'UsageLimited',reason:'The harness has reached its usage allowance.',rule:'usage-limit',observedAt:now,since:now,sequence:1,unseenCompletion:false}};
 const alerts=Array.from({length:20},(_,i)=>({agentId:id,name:i===0?'queuecache':'LongProjectName'.repeat(8),runnerName:'runner-01',harnessId:'codex',kind:'UsageLimit',reason:'The harness has reached its usage allowance.',at:now,token:String(i).padStart(64,'0')}));
 await page.route('**/api/**',async route=>{
  const p=new URL(route.request().url()).pathname;
  let body=p.endsWith('/commands')?[{command:'/model',label:'Choose model'},{command:'/status',label:'Usage and status'},{command:'/diff',label:'View changes'},{command:'/permissions',label:'Permissions'}]:p==='/api/info'?info:p==='/api/agents/attention'?alerts:p===`/api/agents/${id}`?agent:p===`/api/agents/${id}/limit`?null:p==='/api/agents'?[agent]:p.endsWith('/session')?null:[];
  await route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(body)});
 });
 await page.routeWebSocket('**/ws/terminal?**',ws=>{ws.send(JSON.stringify({type:'ready',tty:'/dev/pts/99'}));});
 await page.goto(base+`/agents/${id}`);
 await page.getByLabel('Agents needing attention: 20',{exact:true}).waitFor({timeout:60000});
 for(const width of [1100,740,390,1100]) {
  console.log("Checking notification width",width);
  await page.setViewportSize({width,height:900});
  await page.waitForTimeout(500);
  const before=await page.locator('.terminal-layout').boundingBox();
  await page.getByLabel('Agents needing attention: 20',{exact:true}).click();
  await page.getByRole('menuitem',{name:'View all agent statuses'}).waitFor({timeout:5000}).catch(async e=>{await page.screenshot({path:'/tmp/dam-notification-failure.png'});console.log(await page.locator('.agent-attention-popover').evaluateAll(xs=>xs.map(x=>({class:x.className,rect:x.getBoundingClientRect().toJSON(),style:x.getAttribute('style')}))));throw e;});
  await page.waitForTimeout(300);
  const menu=await page.locator('.agent-attention-popover.mud-popover-open').boundingBox();
  const after=await page.locator('.terminal-layout').boundingBox();
  assert.deepEqual(after,before,'opening notification menu changed terminal geometry');
  assert.ok(menu.x>=0 && menu.x+menu.width<=width+1,'notification menu overflows horizontally');
  assert.ok(menu.y>=0 && menu.y+menu.height<=900,'notification menu overflows vertically');
  assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false,'notification menu expands document');
  await page.screenshot({path:`/tmp/dam-attention-${width}.png`});
  await page.getByLabel('Agents needing attention: 20',{exact:true}).click();
  await page.locator('.agent-attention-popover.mud-popover-open').waitFor({state:'hidden'});
 }
 for (const viewport of [{width:390,height:844},{width:320,height:640},{width:740,height:360},{width:390,height:380}]) {
  await page.setViewportSize(viewport);
  await page.waitForTimeout(500);
  const before=await page.locator('.terminal-layout').boundingBox();
  const button=page.locator('button[aria-label="Harness quick commands"]:visible');
  await button.click();
  await page.getByRole('menuitem',{name:'/permissions · Permissions',exact:true}).waitFor();
  await page.waitForTimeout(300);
  const menu=await page.locator('.quick-command-popover.mud-popover-open').boundingBox();
  assert.ok(menu.x>=0 && menu.x+menu.width<=viewport.width,'quick commands overflow horizontally');
  assert.ok(menu.y>=0 && menu.y+menu.height<=viewport.height,'quick commands overflow vertically');
  assert.ok(menu.width>=260,'commands collapsed to the activator width');
  assert.deepEqual(await page.locator('.terminal-layout').boundingBox(),before,'quick commands resize terminal');
  assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false,'quick commands expand document');
  await page.screenshot({path:`/tmp/dam-commands-${viewport.width}-${viewport.height}.png`});
  await button.click();
  await page.locator('.quick-command-popover.mud-popover-open').waitFor({state:'hidden'});
 }
 console.log('Passed: quick commands fit phone, landscape and reduced keyboard-height viewports.');
 console.log('Passed: notification popover stays inside wide, narrow and rotated touch viewports without resizing the terminal.');
}finally {await browser.close();}
