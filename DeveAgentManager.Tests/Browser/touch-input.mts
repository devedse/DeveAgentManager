// Browser DOM events plus optional integration with an isolated, already-open Codex prompt.
// DAM_INPUT_TMUX_TARGET must name a disposable test session; this test never presses Enter.
import assert from 'node:assert/strict';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {resolve} from 'node:path';
const {chromium} = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
const exec = promisify(execFile);
const target = process.env.DAM_INPUT_TMUX_TARGET;
if (target && !target.startsWith('=dam-review-input:')) throw new Error('Only the isolated dam-review-input session is supported');
const browser = await chromium.launch();
try {
    const page = await browser.newPage({viewport:{width:390,height:844},isMobile:true,hasTouch:true});
    let delivery: Promise<unknown> = Promise.resolve();
    const sent = [];
    await page.exposeFunction('terminalInput', data => {
        sent.push(data);
        if (target) delivery = delivery.then(() => exec('tmux',['send-keys','-t',target,'-l',data]));
    });
    await page.setContent('<main id="terminal"></main>');
    await page.addScriptTag({path:resolve('DeveAgentManager/wwwroot/js/terminal-touch-input.js')});
    await page.evaluate(() => {
        window.adapter = DamTouchInput.attach({options:{}}, document.querySelector('main'), data => window.terminalInput(data));
        adapter.setEnabled(true); adapter.focus();
        window.ime = (value, type) => {
            const box = document.querySelector('textarea');
            box.value=value; box.setSelectionRange(value.length,value.length);
            box.dispatchEvent(new InputEvent('input',{inputType:type,bubbles:true}));
        };
    });
    if (target) {
        await exec('tmux',['send-keys','-t',target,'C-u']);
        await exec('tmux',['send-keys','-t',target,'-l','existing draft ']);
        await page.waitForTimeout(350);
    }
    await page.evaluate(() => ime('        whena','insertText'));
    await page.waitForTimeout(100);
    await page.evaluate(() => ime('when ','insertReplacementText'));
    await page.keyboard.press('Backspace');
    await page.keyboard.press('Backspace');
    await page.waitForTimeout(200);
    await delivery;
    assert.equal(await page.locator('textarea').inputValue(), 'whe');
    assert.equal(sent.join(''), 'whena\x7f \x7f\x7f', 'correction and Backspace must affect only the new word');
    if (target) {
        await page.waitForTimeout(500);
        const {stdout} = await exec('tmux',['capture-pane','-p','-J','-t',target]);
        assert.match(stdout,/› existing draft whe\s*\n/, 'Codex must preserve the earlier draft');
        await exec('tmux',['send-keys','-t',target,'C-u']);
    }
    await page.evaluate(() => adapter.dispose());
    assert.equal(await page.locator('textarea').count(),0);
    console.log(`Passed: native browser Backspace after IME replacement${target ? ', including the real Codex prompt' : ''}.`);
} finally { await browser.close(); }
