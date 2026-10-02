const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';
const browser = await chromium.launch();
try {
let page;
const context = await browser.newContext({viewport:{width:1280,height:900},timezoneId:'Europe/Amsterdam'});
page = await context.newPage();
const errors=[]; page.on('console', msg=>{if (msg.type()==='error' && msg.text().includes('Unhandled exception')) errors.push(msg.text());});page.on('pageerror', e=>errors.push(e.message));
const base=process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
await context.request.post(base+'/account/login',{form:{username:'admin',password:process.env.DAM_TEST_PASSWORD ?? 'preview-test-only',returnUrl:'/'}});
const info=await (await context.request.get(base+'/api/info')).json();
const id='00000000-0000-0000-0000-000000000001', now=new Date().toISOString();
let dismissed=false, limitVisible=true;
let seen=[], schedules=[], limitStatus='Detected', limitDetail=null;
const activity={status:'Ready',reason:"Codex returned a response and its prompt is ready.",rule:'codex/codex-ready-prompt · composer and latest response',evidence:'• done',observedAt:now,since:now,sequence:5,questionWaiting:false,completedAt:now,completionSequence:5,seenCompletionSequence:0,unseenCompletion:true};
const agent={id,runnerId:'00000000-0000-0000-0000-000000000002',runnerName:'runner-01',runnerState:'Ready',runnerHarnessIds:['codex','claude'],runnerToolIds:[],name:'Example agent',harnessId:'codex',workingDirectory:'/work/example',state:'Running',accountName:'Personal',createdAt:now,updatedAt:now,working:false,activity,lastAssistantMessageAt:now};
const runner={id:agent.runnerId,name:'runner-01',providerId:'proxmox',kind:'Headless',state:'Ready',host:'10.0.0.2',sshPort:22,username:'test',cores:4,memoryMb:8192,diskGb:64,harnessIds:['codex','claude'],toolIds:[],agents:[agent],createdAt:now,updatedAt:now,status:{powerState:'Running',cpuUsage:18,memoryUsedBytes:4294967296,memoryTotalBytes:8589934592,diskUsedBytes:21474836480,diskTotalBytes:68719476736}};
await page.route('**/api/**',async route=>{
 const url=new URL(route.request().url()),path=url.pathname;
 let body;
 if(path==='/api/info')body=info;
 else if(path==='/api/agents/attention') body=activity.unseenCompletion&&!dismissed?[{agentId:id,name:agent.name,runnerName:agent.runnerName,harnessId:'codex',kind:'Finished',reason:'Finished since you last viewed this agent.',at:now,completionSequence:5,token:'A'.repeat(64)}]:[];
 else if(path===`/api/agents/${id}/attention/dismiss`){assert.equal(route.request().postDataJSON().token,'A'.repeat(64));dismissed=true;return route.fulfill({status:204});}
 else if(path===`/api/agents/${id}/limit/continue`){schedules.push(route.request().postDataJSON());return route.fulfill({status:204});}
 else if(path===`/api/agents/${id}/seen`){seen.push(route.request().postDataJSON());activity.unseenCompletion=false;activity.seenCompletionSequence=5;return route.fulfill({status:204});}
 else if(path===`/api/agents/${id}`)body=agent;
 else if(path===`/api/agents/${id}/limit`)body=agent.harnessId==='claude'&&limitVisible?{message:"You've hit your weekly limit · resets Oct 4, 3am (Europe/Amsterdam)",timeZoneId:'Europe/Amsterdam',detectedAt:now,resetAt:'2026-10-04T02:00:00Z',detectedResetAt:'2026-10-04T01:00:00Z',continueAt:limitStatus==='Scheduled'?'2026-10-04T01:01:00Z':null,status:limitStatus,detail:limitDetail,nativeAutoContinue:false}:null;
 else if(path==='/api/agents')body=[agent];
 else if(path.includes('/session'))body=null;
 else if(path.includes('/viewers'))body=[];
 else if(path==='/api/runners')body=[runner];
 else if(path===`/api/runners/${runner.id}`)body=runner;
 else if(path===`/api/runners/${runner.id}/usage`)body=Array.from({length:15},(_,i)=>({at:new Date(Date.now()-(15-i)*1000).toISOString(),cpuPercent:18+i,memoryPercent:50,diskPercent:31.25}));
 else if(path===`/api/runners/${runner.id}/timezone`)body={defaultTimeZoneId:'Europe/Amsterdam',overrideTimeZoneId:null};
 else if(path===`/api/runners/${runner.id}/activity` || path===`/api/runners/${runner.id}/log`)body=[];
 else if(path==='/api/sessions')body=[];
 else return route.continue();
 await route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(body)});
});
console.log('Checking activity overview');
await page.goto(base+'/activity');
await page.getByRole('button',{name:'Explain Example agent status: Ready'}).waitFor({timeout:60000});
await page.getByText(activity.reason,{exact:true}).waitFor();
assert.equal(seen.length,0,'activity overview must not mark results seen');
activity.status='Working';activity.reason='The agent is working in the background.';
await page.getByRole('button',{name:'Explain Example agent status: Working'}).waitFor({timeout:15000});
await page.getByText(activity.reason,{exact:true}).waitFor();
await page.setViewportSize({width:390,height:844});
await page.waitForTimeout(500);
await page.screenshot({path:'/tmp/dam-activity-overview-phone.png'});
assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>window.innerWidth),false,'activity overview overflows on phone');
activity.status='Ready';activity.reason='Codex returned a response and its prompt is ready.';
await page.setViewportSize({width:1280,height:900});
await page.goto(base+'/agents');
await page.getByRole('button',{name:'Explain Example agent status: Ready'}).waitFor({timeout:60000});
await page.getByText('Last reply', {exact:false}).waitFor();
await page.setViewportSize({width:390,height:844});
await page.getByLabel('Agents needing attention: 1',{exact:true}).click();
await page.getByRole('menuitem',{name:'View all agent statuses'}).waitFor();
await page.getByText('Example agent · Finished',{exact:true}).waitFor();
await page.waitForTimeout(500); // Allow the drawer and popover to settle after changing viewport size.
assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>window.innerWidth),false,'attention dismissal overflows on phone');
await page.screenshot({path:'/tmp/dam-dismiss-phone.png'});
await page.getByRole('button',{name:'Dismiss Example agent notification',exact:true}).click();
await page.getByLabel('Agents needing attention: 0',{exact:true}).waitFor();
assert.equal(new URL(page.url()).pathname,'/agents','dismissing must not navigate to the terminal');
assert.equal(seen.length,0,'dismissing a notification must not acknowledge a terminal view');
await page.setViewportSize({width:1280,height:900});
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
console.log('Checking manual schedule');
// No limit is required for an explicit manual test.
await page.getByRole('button',{name:'Schedule continue',exact:true}).click();
await page.getByText('Manual continuation — no usage limit is required.',{exact:true}).waitFor();
assert.equal(await page.getByRole('spinbutton',{name:'Continue in',exact:true}).inputValue(),'1');
await page.locator('.mud-dialog').getByRole('button',{name:'Schedule continue',exact:true}).click();
await page.waitForTimeout(300);
assert.equal(schedules[0].manual,true);
assert.equal(schedules[0].continueAfterMinutes,1);
schedules=[];
console.log('Checking detected limits and outcomes');
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
await page.getByText('Your local timezone: Europe/Amsterdam',{exact:true}).waitFor();
assert.equal(await page.getByRole('textbox',{name:'Reset time',exact:true}).inputValue(),'04:00');
await page.getByRole('button',{name:'Use detected reset (+1 minute)',exact:true}).click();
assert.equal(await page.getByRole('textbox',{name:'Reset time',exact:true}).inputValue(),'03:00');
await page.getByText('Continue at 2026-10-04 03:01 (Europe/Amsterdam).',{exact:true}).waitFor();
await page.getByRole('textbox',{name:'Reset date',exact:true}).fill('2026-10-04');
await page.getByRole('textbox',{name:'Reset time',exact:true}).fill('03:00');
await page.getByRole('textbox',{name:'Reset time',exact:true}).press('Tab');
await page.getByRole('button',{name:'Schedule continue',exact:true}).click();
await page.waitForTimeout(300);
assert.equal(Date.parse(schedules[0].resetAtUtc),Date.parse('2026-10-04T01:00:00Z'));
await page.getByRole('button',{name:'Usage limit: schedule continue after reset',exact:true}).click();
await page.getByRole('switch',{name:'Continue after a duration'}).check();
await page.getByRole('spinbutton',{name:'Continue in',exact:true}).fill('5');
await page.getByRole('button',{name:'Schedule continue',exact:true}).click();
await page.waitForTimeout(300);
assert.equal(schedules[1].continueAfterMinutes,5,'duration must be sent to the Manager for calculation using its clock');
for (const [status,hint] of [['Sent','Continue submitted; view result'],['Cancelled','Continue cancelled; view reason'],['Uncertain','Continue delivery uncertain; check the terminal']]) {
 limitStatus=status; limitDetail='Result of scheduled attempt: '+status+'.';
 await page.getByRole('button',{name:hint,exact:true}).waitFor({timeout:15000});
 await page.getByRole('button',{name:hint,exact:true}).click();
 await page.getByText(limitDetail,{exact:true}).waitFor();
 assert.equal(await page.getByRole('button',{name:'Schedule continue',exact:true}).count(),status==='Cancelled'?1:0);
 await page.getByRole('button',{name:'Close',exact:true}).last().click();
}

limitStatus='Scheduled';limitDetail=null;
await page.getByRole('button',{name:'Continue scheduled for 2026-10-04 03:01 (Europe/Amsterdam)',exact:true}).waitFor({timeout:15000});
await page.getByRole('button',{name:'Continue scheduled for 2026-10-04 03:01 (Europe/Amsterdam)',exact:true}).click();
await page.getByText('2026-10-04 03:01 (Europe/Amsterdam)',{exact:true}).waitFor();
await page.getByRole('button',{name:'Close',exact:true}).last().click();

limitVisible=false;
await page.getByRole('button',{name:'Schedule continue',exact:true}).waitFor({timeout:15000});
assert.equal(await page.getByRole('button',{name:'Schedule continue',exact:true}).evaluate(e=>e.classList.contains('mud-warning-text')),false);
await page.getByRole('button',{name:'Schedule continue',exact:true}).click();
await page.getByText('Manual continuation — no usage limit is required.',{exact:true}).waitFor();
assert.equal(await page.getByText("You've hit your weekly limit",{exact:false}).count(),0);
await page.getByRole('button',{name:'Close',exact:true}).last().click();

for (const path of ['/', '/runners', `/runners/${runner.id}`]) {
 await page.goto(base+path);
 await page.locator('.usage-label').filter({hasText:'Disk'}).first().waitFor();
 assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>window.innerWidth),false,'disk usage overflows on '+path);
}
for (const viewport of [{width:390,height:844},{width:1280,height:900}]) {
 await page.setViewportSize(viewport); await page.waitForTimeout(500);
 const bars=await page.locator('.usage:not(.usage-compact) .usage-track').evaluateAll(elements=>elements.map(e=>({x:e.getBoundingClientRect().x,width:e.getBoundingClientRect().width})));
 assert.equal(bars.length,3);
 assert.ok(bars.every(b=>Math.abs(b.x-bars[0].x)<1&&Math.abs(b.width-bars[0].width)<1),'CPU, RAM and disk tracks must align');
}
await page.setViewportSize({width:390,height:844});
await page.locator('.usage-chart-line-disk').first().waitFor({state:'attached'});
assert.equal(await page.locator('.usage-chart-line-disk').first().evaluate(p=>getComputedStyle(p).stroke),'rgb(245, 158, 11)');
await page.getByRole('button',{name:'1 min',exact:true}).click();
await page.locator('.usage-chart-line-disk').first().waitFor({state:'attached'});
assert.equal(await page.locator('.usage-chart-line-disk').first().evaluate(p=>getComputedStyle(p).stroke),'rgb(245, 158, 11)');
await page.screenshot({path:'/tmp/dam-disk-phone.png'});
assert.deepEqual(errors,[]);
console.log('Passed: activity overview polling without a terminal, phone layout, attention and completion guards, latest reply label, local reset picker, duration scheduling, disk bars and graph.');
} finally { await browser.close(); }
