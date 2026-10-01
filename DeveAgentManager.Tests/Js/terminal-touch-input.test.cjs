const assert = require('node:assert/strict');
const { attach } = require(process.argv[2]);

class Target {
    constructor() { this.listeners = new Map(); }
    addEventListener(type, callback) {
        const callbacks = this.listeners.get(type) ?? [];
        callbacks.push(callback); this.listeners.set(type, callbacks);
    }
    removeEventListener(type, callback) {
        this.listeners.set(type, (this.listeners.get(type) ?? []).filter(c => c !== callback));
    }
    emit(type, extra = {}) {
        const event = { type, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; }, stopPropagation() {}, ...extra };
        for (const callback of this.listeners.get(type) ?? []) callback(event);
        return event;
    }
}
class Textarea extends Target {
    value = ''; selectionStart = 0; selectionEnd = 0; style = {};
    setAttribute() {}
    setSelectionRange(start, end) { this.selectionStart = start; this.selectionEnd = end; }
    focus() { document.activeElement = this; }
    remove() { this.removed = true; }
}
function fixture(initial = '') {
    const doc = new Target();
    doc.createElement = () => new Textarea();
    global.document = doc;
    let box;
    let text = Array.from(initial), cursor = text.length;
    const sent = [];
    const adapter = attach({ options: {} }, { appendChild(value) { box = value; } }, data => {
        sent.push(data);
        // An ordinary terminal editor, including text that predates this textarea's tracking.
        for (let i = 0; i < data.length;) {
            if (data.startsWith('\x1b[D', i)) { cursor = Math.max(0, cursor - 1); i += 3; }
            else if (data.startsWith('\x1b[C', i)) { cursor = Math.min(text.length, cursor + 1); i += 3; }
            else if (data[i] === '\x7f') { if (cursor) text.splice(--cursor, 1); i++; }
            else { const char = String.fromCodePoint(data.codePointAt(i)); text.splice(cursor++, 0, char); i += char.length; }
        }
    });
    adapter.setEnabled(true); box.focus();
    return {
        box, sent, adapter,
        input(value, inputType = 'insertText', caret = value.length) {
            box.value = value; box.setSelectionRange(caret, caret); box.emit('input', { inputType });
        },
        text: () => text.join('')
    };
}
const pad = ' '.repeat(8);
async function run() {
{
    const f = fixture('existing draft ');
    f.input(pad + 'whena');
    f.input('when ', 'insertReplacementText');
    // Some keyboards retain their own unpadded context after a script rewrites the field.
    f.input('when', 'deleteContentBackward');
    f.input('whe', 'deleteContentBackward');
    assert.equal(f.text(), 'existing draft whe', 'Backspace must not erase earlier text when the IME keeps unpadded context');
}
{
    const f = fixture('earlier ');
    f.input(pad + 'whena');
    f.box.emit('compositionstart');
    f.input('', 'deleteCompositionText');
    assert.equal(f.text(), 'earlier ', 'clearing IME context must not erase an untracked draft');
    f.input('when', 'insertCompositionText');
    f.box.emit('compositionend');
    await new Promise(resolve => setTimeout(resolve, 5));
    f.input('whe', 'deleteContentBackward');
    assert.equal(f.text(), 'earlier whe');
    f.input('', 'deleteWordBackward');
    assert.equal(f.text(), 'earlier ');
    assert.equal(f.box.emit('beforeinput', {inputType:'deleteContentBackward'}).defaultPrevented, true);
    assert.equal(f.text(), 'earlier', 'Backspace on an empty unpadded field reaches the earlier draft exactly once');
}
{
    const f = fixture();
    f.input(pad + 'whena');
    f.input('when', 'insertReplacementText');
    f.box.setSelectionRange(2, 2);
    document.emit('selectionchange');
    f.input('wxhen', 'insertText', 2);
    assert.equal(f.text(), 'wxhen', 'moving the caret after autocorrect remains synchronized');
    f.box.emit('compositionend');
    const count = f.sent.length;
    f.adapter.dispose();
    await new Promise(resolve => setTimeout(resolve, 5));
    assert.equal(f.sent.length, count, 'disposed adapters must not send delayed composition changes');
    assert.equal(document.listeners.get('selectionchange').length, 0);
    assert.equal(f.box.removed, true);
}
{
    const f = fixture('existing draft ');
    f.input(pad + 'whena');
    f.input(pad + 'when ', 'insertReplacementText');
    f.input(pad + 'when', 'deleteContentBackward');
    f.input(pad + 'whe', 'deleteContentBackward');
    assert.equal(f.text(), 'existing draft whe', 'autocorrect then repeated Backspace');
}
{
    const f = fixture('existing draft ');
    f.input(pad + 'whena');
    // A keyboard replaces its field context as well as the word. Virtual padding is not real draft text.
    f.input('when ', 'insertReplacementText');
    assert.equal(f.text(), 'existing draft when ', 'replacement must preserve the earlier draft');
    f.input(pad + 'when', 'deleteContentBackward');
    f.input(pad + 'whe', 'deleteContentBackward');
    assert.equal(f.text(), 'existing draft whe', 'Backspace after replacement must stay synchronized');
}
{
    const f = fixture();
    f.input(pad + 'whena');
    f.box.emit('compositionstart');
    f.input('when ', 'insertCompositionText');
    assert.equal(f.box.value, 'when ', 'do not rewrite the IME field during composition');
    f.box.emit('compositionend');
    await new Promise(resolve => setTimeout(resolve, 5));
    assert.equal(f.box.value, 'when ', 'do not rewrite the keyboard context after composition');
    f.input(pad + 'when ', 'insertText');
    f.input(pad + 'whena', 'insertReplacementText'); // undo autocorrect
    f.input(pad + 'when', 'deleteContentBackward');
    assert.equal(f.text(), 'when', 'composition completion, correction undo and Backspace');
}
{
    const f = fixture('old');
    f.input(pad.slice(1), 'deleteContentBackward');
    assert.equal(f.text(), 'ol', 'Backspace into padding still deletes untracked draft text');
}
console.log('Touch input regression cases passed');
}
run().catch(error => { console.error(error); process.exitCode = 1; });
