const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';
const browser = await chromium.launch();
try {
 const context = await browser.newContext({viewport:{width:390,height:844},hasTouch:true});
 const page = await context.newPage();
 const errors=[]; page.on('pageerror',e=>errors.push(e.message));
 const base=process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
 await context.request.post(base+'/account/login',{form:{username:'admin',password:process.env.DAM_TEST_PASSWORD ?? 'preview-test-only',returnUrl:'/'}});
 const info=await (await context.request.get(base+'/api/info')).json();
 const id='00000000-0000-0000-0000-000000000001', other='00000000-0000-0000-0000-000000000002',now=new Date().toISOString();
 const agent={id,runnerId:id,runnerName:'runner-01',runnerState:'Ready',runnerHarnessIds:['codex','claude'],runnerToolIds:['vscode'],name:'deveagentmanager',harnessId:'codex',workingDirectory:'/work/deveagentmanager',state:'Running',accountName:'Personal',createdAt:now,updatedAt:now,activity:{status:'UsageLimited',reason:'Usage allowance reached.',rule:'usage-limit',observedAt:now,since:now,sequence:1}};
 let rows=[{id,title:'Current conversation',agentId:id,agent},{id:other,title:'Earlier conversation',agentId:null,agent:null}].map(s=>({...s,workspaceAgentId:id,workspaceName:agent.name,harnessId:'codex',runnerName:'runner-01',repositoryUrl:'https://example.com/repository',workingDirectory:'/work/deveagentmanager',messageCount:12,createdAt:now,updatedAt:now,lastSyncedAt:now,sourceSize:2048,totalSize:4096}));
 const actions=[];
 await page.route('**/api/**',async route=>{
  const p=new URL(route.request().url()).pathname, method=route.request().method();
  if(p.endsWith('/session')&&method==='POST') {
   const request=route.request().postDataJSON();actions.push(request);
   if(request.sessionId===null) rows.push({...rows[0],id:'00000000-0000-0000-0000-000000000003',title:request.title??'New session',agentId:null});
   const selected=request.sessionId??rows.at(-1).id;
   rows.forEach(s=>{s.agentId=s.id===selected?id:null;s.agent=s.id===selected?agent:null;});
   return route.fulfill({status:204});
  }
  if(p.endsWith('/title')&&method==='PUT') {rows.find(s=>p.includes(s.id)).title=route.request().postDataJSON().title;return route.fulfill({status:204});}
  if(p.startsWith('/api/sessions/')&&method==='DELETE') {rows=rows.filter(s=>!p.endsWith(s.id));return route.fulfill({status:204});}
  const body=p==='/api/info'?info:p==='/api/sessions'?rows:p.endsWith('/session')?rows.find(s=>s.agentId===id):p===`/api/agents/${id}`?agent:p==='/api/agents'?[agent]:p.endsWith('/limit/automatic')?{enabled:false,sent:0}:p.endsWith('/limit')?null:[];
  await route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(body)});
 });
 await page.routeWebSocket('**/ws/terminal?**',ws=>ws.send(JSON.stringify({type:'ready',tty:'/dev/pts/99'})));
 await page.goto(base+`/agents/${id}`);
 await page.getByLabel('Switch session',{exact:true}).waitFor({timeout:60000});
 for(const width of [320,390,600,740,1280]) {
  await page.setViewportSize({width,height:844});await page.waitForTimeout(300);
  const boxes=await page.locator('.terminal-toolbar').evaluate(el=>[...el.children].filter(x=>getComputedStyle(x).display!=='none'&&x.getBoundingClientRect().width>0).map(x=>{const r=x.getBoundingClientRect();return {x:r.x,right:r.right};}));
  assert.ok(boxes.every(b=>b.x>=0&&b.right<=width+1),`toolbar overflows at ${width}: ${JSON.stringify(boxes)}`);
  const title=await page.locator('.terminal-title').boundingBox();
  const status=await page.locator('.terminal-name-row .agent-status-button').boundingBox();
  assert.ok(status.x+status.width<=title.x+title.width+1,`status escapes title at ${width}`);
  await page.getByLabel('Switch session',{exact:true}).click();
  await page.getByText('Earlier conversation',{exact:true}).waitFor();
  const dialog=await page.getByRole('dialog').boundingBox();
  assert.ok(dialog.x>=0&&dialog.x+dialog.width<=width,`dialog overflows at ${width}`);
  assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);
  await page.screenshot({path:`/tmp/dam-sessions-${width}.png`});
  await page.getByRole('button',{name:'Close',exact:true}).last().click();
 }
 await page.setViewportSize({width:390,height:844});
 await page.getByLabel('Switch session',{exact:true}).click();
 const oldRow=page.locator('.session-picker-row').filter({hasText:'Earlier conversation'});
 await oldRow.getByLabel('Rename session').click();
 await page.getByLabel('Session name',{exact:true}).fill('Renamed history');
 await page.getByRole('button',{name:'Save',exact:true}).click();
 await page.getByText('Renamed history',{exact:true}).waitFor();
 await page.locator('.session-picker-row').filter({hasText:'Renamed history'}).getByRole('button',{name:'Select',exact:true}).click();
 await page.getByRole('dialog').waitFor({state:'hidden'});
 assert.equal(actions.at(-1).sessionId,other);
 await page.getByLabel('Switch session',{exact:true}).click();
 await page.getByLabel('New session name (optional)',{exact:true}).fill('Fresh chat');
 await page.getByRole('button',{name:'New session',exact:true}).click();
 await page.getByRole('dialog').waitFor({state:'hidden'});
 assert.equal(actions.at(-1).title,'Fresh chat');
 await page.getByLabel('Switch session',{exact:true}).click();
 await page.locator('.session-picker-row').filter({hasText:'Renamed history'}).getByLabel('Delete session').click();
 await page.getByRole('button',{name:'Cancel',exact:true}).click();
 assert.equal(rows.length,3);
 await page.locator('.session-picker-row').filter({hasText:'Renamed history'}).getByLabel('Delete session').click();
 await page.getByRole('button',{name:'Delete',exact:true}).click();
 await page.getByText('Renamed history',{exact:true}).waitFor({state:'hidden'});
 assert.equal(rows.length,2);
 await page.getByRole('button',{name:'Close',exact:true}).last().click();
 await page.goto(base+'/sessions');
 await page.getByText('Manage sessions',{exact:true}).waitFor();
 assert.equal(await page.locator('.session-group-heading').count(),1);
 assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);
 await page.screenshot({path:'/tmp/dam-sessions-page-phone.png'});
 assert.deepEqual(errors,[]);
 console.log('Passed: header and picker fit 320–1280px; rename, select, new, confirmed deletion and grouped sessions page.');
} finally {await browser.close();}
