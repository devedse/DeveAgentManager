// Real scope persistence and mocked login/Runner metadata; no model request or live credential is used.
import assert from 'node:assert/strict';
const {chromium}=await import(process.env.PLAYWRIGHT_MODULE??'playwright');
const browser=await chromium.launch();
const base=process.env.DAM_TEST_URL??'http://127.0.0.1:19081';
const id=(n:number)=>`00000000-0000-0000-0000-${String(n).padStart(12,'0')}`;
try{
    const context=await browser.newContext({viewport:{width:1280,height:960}});
    const page=await context.newPage();
    const errors:string[]=[];page.on('pageerror',e=>errors.push(e.message));
    await page.goto(base+'/login');
    await page.locator('[name=username]').fill('admin');
    await page.locator('[name=password]').fill(process.env.DAM_TEST_PASSWORD??'test-only-password');
    await Promise.all([page.waitForURL(base+'/'),page.locator('button[type=submit]').click()]);
    const endpoint='/api/me/settings/models',headers={'X-DAM-Request':'1',Origin:base};
    const profile=await(await context.request.get(base+'/api/settings/profile')).json();
    const original=structuredClone(profile);
    profile.harnesses.pi={...(profile.harnesses.pi??{}),scopedModels:['claude-code/*:high']};
    assert.equal((await context.request.put(base+'/api/settings/profile',{data:profile,headers})).status(),200);
    await page.goto(base+'/settings?tab=models');
    const section=page.locator('[data-section=model-defaults]'),pi=section.locator('[data-harness=pi]');
    const scope=pi.locator('[data-section=pi-model-scope]');
    const choose=async(label:string)=>{await scope.locator('.mud-select').first().click();await page.getByRole('option',{name:label,exact:true}).click();};
    const save=async()=>{const response=page.waitForResponse(r=>new URL(r.url()).pathname===endpoint&&r.request().method()==='PUT');await section.getByRole('button',{name:'Save',exact:true}).click();assert.equal((await response).status(),200);};
    const current=async()=>(await(await context.request.get(base+endpoint)).json()).find(x=>x.harnessId==='pi');
    await scope.getByText('Manager default: 1 pattern.',{exact:true}).waitFor();
    await choose('Choose model patterns');
    const patterns=scope.getByLabel('Model patterns',{exact:true});
    await patterns.fill('dam-codex/*:high\nclaude-code/default');await save();
    assert.deepEqual((await current()).scopedModels,['dam-codex/*:high','claude-code/default']);
    await page.reload();await patterns.waitFor();
    assert.equal(await patterns.inputValue(),'dam-codex/*:high\nclaude-code/default');
    await patterns.fill('bad pattern');
    assert.equal(await section.getByRole('button',{name:'Save',exact:true}).isDisabled(),true);
    await patterns.fill('dam-codex/*:high\nclaude-code/default');
    for(const theme of ['light','dark'])for(const width of [390,320]){
        await page.evaluate(t=>localStorage.setItem('dam-theme',t),theme);
        await page.setViewportSize({width,height:900});await page.reload();await patterns.waitFor();
        await scope.scrollIntoViewIfNeeded();
        assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),`${theme}/${width} scope fits`);
        await page.screenshot({path:`/tmp/dam-pi-model-scope-${theme}-${width}.png`,fullPage:true});
    }
    await choose('All available models');await save();assert.deepEqual((await current()).scopedModels,[]);
    await choose('Use Manager default');await save();assert.equal((await current()).scopedModels,null);
    console.log('Passed: scoped-model order, save/reload, validation, all/inherit and phone themes.');
    const me=await(await context.request.get(base+'/api/me')).json(),now=new Date().toISOString();
    const agent:any={id:id(61),ownerId:me.id,ownerName:'Test owner',access:'Owner',name:'pi-preview',harnessId:'pi',runnerId:id(62),runnerName:'Test runner',runnerState:'Ready',runnerHarnessIds:['pi','claude','codex'],runnerToolIds:[],state:'Running',workingDirectory:'/workspace/example',accountId:null,runningAccountId:null,canUseRunner:true,createdAt:now,updatedAt:now};
    const accounts=[
        {id:id(63),harnessId:'claude',name:'Claude first',kind:'Subscription'},
        {id:id(64),harnessId:'codex',name:'Codex first',kind:'Subscription'},
        {id:id(65),harnessId:'claude',name:'API key must stay out',kind:'ApiKey'},
        {id:id(66),harnessId:'claude',name:'Claude second',kind:'Subscription'},
        {id:id(67),harnessId:'codex',name:'Codex second',kind:'Subscription'},
    ].map(a=>({...a,createdByUsername:'testuser',access:'Use',enabled:true,createdAt:now,updatedAt:now}));
    const posts:any[]=[],saves:any[]=[];
    let rows=await(await context.request.get(base+endpoint)).json();
    let accountFailure=false,slowAccounts=false;
    await context.route('**/api/**',async route=>{
        const request=route.request(),path=new URL(request.url()).pathname;
        if(path==='/api/accounts'){
            if(accountFailure){return route.fulfill({status:503,json:{error:'Test login list unavailable'}});}
            if(slowAccounts)await new Promise(done=>setTimeout(done,1100));
            return route.fulfill({json:accounts});
        }
        if(path===endpoint){
            if(request.method()==='PUT'){
                const data=request.postDataJSON();saves.push(data);
                rows=rows.map(row=>({...row,...(data[row.harnessId]??{model:null,effort:null,scopedModels:null,piAccountIds:null})}));
            }
            return route.fulfill({json:rows});
        }
        if(path===`/api/agents/${agent.id}/switch`){posts.push(request.postDataJSON());return route.fulfill({status:204});}
        if(path===`/api/agents/${agent.id}`)return route.fulfill({json:agent});
        if(path==='/api/agents')return route.fulfill({json:[agent]});
        if(path.startsWith(`/api/agents/${agent.id}/`))return route.fulfill({json:path.endsWith('/limit/automatic')?{enabled:false,sent:0}:null});
        return route.continue();
    });
    await context.routeWebSocket('**/ws/terminal?**',ws=>ws.send(JSON.stringify({type:'ready',tty:'/dev/pts/99'})));
    await page.setViewportSize({width:390,height:900});await page.goto(base+`/agents/${agent.id}`);
    await page.locator('.switch-chip').click();
    const menu=page.locator('.switch-menu:visible');
    await menu.locator('.pi-switch-row .switch-harness-button').waitFor();
    assert.equal((await menu.locator('.pi-switch-row .switch-option-label').textContent()).trim(),'pi');
    assert.equal(await menu.getByText('Saved login (preview)',{exact:true}).count(),0);
    assert.equal(await menu.getByText('Runner logins (/login)',{exact:true}).count(),0);
    accountFailure=true;
    await menu.getByRole('link',{name:'pi settings',exact:true}).click();
    await page.waitForURL(base+'/settings?tab=models');
    const logins=page.locator('[data-section=pi-logins]');
    await logins.getByRole('button',{name:'Retry',exact:true}).waitFor();
    assert.equal(await logins.getByText('No saved subscriptions available.',{exact:true}).count(),0);
    accountFailure=false;slowAccounts=true;
    await logins.getByRole('button',{name:'Retry',exact:true}).click();
    await logins.getByRole('status').waitFor();
    assert.equal(await logins.getByText('No saved subscriptions available.',{exact:true}).count(),0);
    await logins.getByRole('checkbox',{name:/Claude first/}).waitFor();
    slowAccounts=false;
    assert.equal(await logins.getByRole('checkbox',{name:/API key must stay out/}).count(),0);
    for(const name of ['Claude first','Claude second','Codex first','Codex second'])await logins.getByRole('checkbox',{name:new RegExp(name)}).check();
    assert.equal(posts.length,0,'settings and selections never switch or restart an agent');
    assert.equal(saves.length,0,'checkbox drafts wait for Save');
    await save();
    assert.deepEqual(new Set(saves.at(-1).pi.piAccountIds),new Set([id(63),id(64),id(66),id(67)]));
    await page.reload();
    for(const name of ['Claude first','Claude second','Codex first','Codex second'])assert.equal(await logins.getByRole('checkbox',{name:new RegExp(name)}).isChecked(),true);
    await pi.getByRole('button',{name:'Add selected logins to model cycling',exact:true}).click();
    assert.match(await patterns.inputValue(),/dam-claude-00000000000000000000000000000063\/\*/);
    assert.match(await patterns.inputValue(),/dam-codex-00000000000000000000000000000067\/\*/);
    await save();
    await pi.getByRole('button',{name:'Use Manager default',exact:true}).click();
    assert.equal(await logins.getByRole('checkbox',{name:/Claude first/}).isChecked(),true,'reset model defaults retains forwarded logins');
    for(const theme of ['light','dark'])for(const width of [390,320]){
        await page.evaluate(t=>localStorage.setItem('dam-theme',t),theme);
        await page.setViewportSize({width,height:1100});await page.reload();
        await logins.getByRole('checkbox',{name:/Codex second/}).waitFor();
        await logins.scrollIntoViewIfNeeded();
        assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),`${theme}/${width} forwarded logins fit`);
        await page.screenshot({path:`/tmp/dam-pi-logins-${theme}-${width}.png`,fullPage:true});
    }
    await logins.getByRole('checkbox',{name:/Codex second/}).uncheck();await save();
    assert.equal(saves.at(-1).pi.piAccountIds.includes(id(67)),false);
    assert.equal(posts.length,0,'saving does not execute a harness operation');
    assert.deepEqual(errors,[]);
    assert.equal((await context.request.put(base+'/api/settings/profile',{data:original,headers})).status(),200);
    assert.equal((await context.request.put(base+endpoint,{data:{},headers})).status(),200);
    console.log('Passed: simple pi menu, settings cogwheel, login loading/retry, four accounts, cycle patterns and phone themes.');
}finally{await browser.close();}
