const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';
const browser = await chromium.launch();
try {
let page;
const context = await browser.newContext({viewport:{width:1280,height:900}});
page = await context.newPage();
const errors=[]; page.on('console', msg=>{if (msg.type()==='error' && msg.text().includes('Unhandled exception')) errors.push(msg.text());});page.on('pageerror', e=>errors.push(e.message));
const base=process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
await context.request.post(base+'/account/login',{form:{username:'admin',password:process.env.DAM_TEST_PASSWORD ?? 'preview-test-only',returnUrl:'/'}});
const info=await (await context.request.get(base+'/api/info')).json();
const id='00000000-0000-0000-0000-000000000001', now=new Date().toISOString();
let seen=[];
const activity={status:'Ready',reason:"Codex returned a response and its prompt is ready.",rule:'codex/codex-ready-prompt · composer and latest response',evidence:'• done',observedAt:now,since:now,sequence:5,questionWaiting:false,completedAt:now,completionSequence:5,seenCompletionSequence:0,unseenCompletion:true};
const agent={id,runnerId:'00000000-0000-0000-0000-000000000002',runnerName:'runner-01',runnerState:'Ready',runnerHarnessIds:['codex','claude'],runnerToolIds:[],name:'Example agent',harnessId:'codex',workingDirectory:'/work/example',state:'Running',accountName:'Personal',createdAt:now,updatedAt:now,working:false,activity};
await page.route('**/api/**',async route=>{
 const url=new URL(route.request().url()),path=url.pathname;
 let body;
 if(path==='/api/info')body=info;
 else if(path==='/api/agents/attention') body=activity.unseenCompletion?[{agentId:id,name:agent.name,runnerName:agent.runnerName,harnessId:'codex',kind:'Finished',reason:'Finished since you last viewed this agent.',at:now,completionSequence:5}]:[];
 else if(path===`/api/agents/${id}/seen`){seen.push(route.request().postDataJSON());activity.unseenCompletion=false;activity.seenCompletionSequence=5;return route.fulfill({status:204});}
 else if(path===`/api/agents/${id}`)body=agent;
 else if(path===`/api/agents/${id}/limit`)body=agent.harnessId==='claude'?{message:"You've hit your weekly limit · resets Oct 4, 3am (Europe/Amsterdam)",timeZoneId:'Europe/Amsterdam',detectedAt:now,resetAt:'2026-10-04T01:00:00Z',status:'Detected',nativeAutoContinue:false}:null;
 else if(path==='/api/agents')body=[agent];
 else if(path.includes('/session'))body=null;
 else if(path.includes('/viewers'))body=[];
 else if(path==='/api/runners')body=[];
 else return route.continue();
 await route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(body)});
});
await page.goto(base+'/agents');
await page.getByRole('button',{name:'Explain Example agent status: Ready'}).waitFor({timeout:60000});
await page.getByLabel('Agents needing attention: 1',{exact:true}).click();
await page.getByText('Example agent · Finished',{exact:true}).waitFor();
await page.keyboard.press('Escape');
await page.getByRole('button',{name:'Explain Example agent status: Ready'}).click();
await page.getByText(activity.reason,{exact:true}).waitFor();
await page.getByText('Detection details',{exact:true}).click();
await page.getByText('Rule: '+activity.rule,{exact:true}).waitFor();
await page.screenshot({path:'/tmp/dam-status-desktop.png'});
await page.getByRole('button',{name:'Close',exact:true}).last().click();
// A hidden terminal must not acknowledge completion, even with focus.
await page.addInitScript(()=>{ document.hasFocus=()=>true; Object.defineProperty(document,'visibilityState',{configurable:true,get:()=> 'hidden'}); });
await page.goto(base+`/agents/${id}`);

await page.getByRole('button',{name:'Explain Example agent status: Ready'}).waitFor();
await page.waitForTimeout(5500);
assert.equal(seen.length,0,'background/unfocused terminal acknowledged completion');
await page.evaluate(()=>{Object.defineProperty(document,'visibilityState',{configurable:true,get:()=> 'visible'}); document.hasFocus=()=>false;});
await page.waitForTimeout(5500);
assert.equal(seen.length,0,'unfocused terminal acknowledged completion');
await page.evaluate(()=>{document.hasFocus=()=>true;});
await page.waitForTimeout(5500);
assert.deepEqual(seen,[{completionSequence:5}]);
// Claude limit UI, including the phone toolbar.
agent.harnessId='claude';activity.status='UsageLimited';activity.reason='The harness has reached its usage allowance.';activity.rule='claude/usage-limit · current error';
await page.setViewportSize({width:390,height:844});
await page.goto(base+`/agents/${id}`);
await page.getByRole('button',{name:'Usage limit: schedule continue after reset',exact:true}).waitFor();
assert.ok(await page.locator('.limit-alert-blink').count());
await page.getByRole('button',{name:'Usage limit: schedule continue after reset',exact:true}).click();
await page.getByText("You've hit your weekly limit · resets Oct 4, 3am (Europe/Amsterdam)",{exact:true}).waitFor();
await page.screenshot({path:'/tmp/dam-claude-limit-phone.png'});
assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>window.innerWidth),false,'horizontal overflow');
assert.deepEqual(errors,[]);
console.log('Passed: attention menu, status explanation, hidden/unfocused completion guard, visible acknowledgement, blinking Claude limit button and phone dialog.');
} finally { await browser.close(); }
