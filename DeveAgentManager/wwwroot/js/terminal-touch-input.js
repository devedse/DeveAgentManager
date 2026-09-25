// Typing on phones and tablets without garbled text.
//
// xterm.js reads the keyboard through a hidden textarea that it empties after every character. Phone keyboards
// (Gboard, SwiftKey, Samsung, iOS) don't send key presses: they edit the text they believe is in that field. They
// keep a "word in progress", replace whole words (autocorrect, predictions, swipe typing) and turn Backspace into
// "delete some text". Once xterm.js has emptied the field, the keyboard and the terminal disagree about the text,
// which gives duplicated, dropped and re-added words (xterm.js issues #3600, #4173).
//
// Instead, the keyboard types into our own textarea, which keeps the text of the current line. After every change,
// the old and new text are compared and the difference is sent: arrows to the place that changed, one Backspace per
// removed character and then the new characters. When autocorrect turns "teh" into "the", the terminal gets three
// Backspaces and "the". It is reset (emptied) whenever the line may have changed outside it: Enter, Tab, Esc, Up/Down,
// the on-screen keys and pastes.
//
// The field always starts with a few spaces (the "cushion"), so Backspace on an empty line still deletes something
// the keyboard can see; deleting into the cushion sends Backspaces for text that was on the line before. The cushion
// is refilled afterwards.
(function (root) {
    const CUSHION = 8;
    const BACKSPACE = "\x7f";
    const LEFT = "\x1b[D";
    const RIGHT = "\x1b[C";

    const segmenter = typeof Intl !== "undefined" && Intl.Segmenter ? new Intl.Segmenter(undefined, { granularity: "grapheme" }) : null;

    // Characters as the terminal program counts them for Backspace and arrows: graphemes ("é", "👍🏽" are one).
    function graphemes(text) {
        return segmenter ? Array.from(segmenter.segment(text), s => s.segment) : Array.from(text);
    }

    // How to turn the text `before` into `after` with the terminal cursor at `cursor` (a grapheme index in `before`).
    // Returns the data to send and where the cursor ends up (a grapheme index in `after`).
    function edit(before, after, cursor) {
        let prefix = 0;
        const max = Math.min(before.length, after.length);
        while (prefix < max && before[prefix] === after[prefix]) {
            prefix++;
        }

        let suffix = 0;
        while (suffix < max - prefix && before[before.length - 1 - suffix] === after[after.length - 1 - suffix]) {
            suffix++;
        }

        const removedEnd = before.length - suffix;
        const inserted = after.slice(prefix, after.length - suffix);
        if (removedEnd === prefix && inserted.length === 0) {
            return { data: "", cursor };
        }

        let data = move(cursor, removedEnd);
        data += BACKSPACE.repeat(removedEnd - prefix);
        data += inserted.join("");
        return { data, cursor: prefix + inserted.length };
    }

    function move(from, to) {
        return from < to ? RIGHT.repeat(to - from) : LEFT.repeat(from - to);
    }

    // Keys that are sent as they are and may change the line in ways the field can't follow, so it is reset after them.
    function keySequence(event) {
        const { key, ctrlKey, altKey, metaKey, shiftKey } = event;
        if (metaKey) {
            return null;
        }

        const named = {
            Enter: shiftKey ? "\x1b[13;2u" : "\r",
            Tab: shiftKey ? "\x1b[Z" : "\t",
            Escape: "\x1b",
            ArrowUp: "\x1b[A",
            ArrowDown: "\x1b[B",
            PageUp: "\x1b[5~",
            PageDown: "\x1b[6~",
        }[key];
        if (named && !ctrlKey && !altKey) {
            return named;
        }

        if (key.length === 1 && ctrlKey && !altKey) {
            const code = key.toUpperCase().charCodeAt(0);
            if (code >= 64 && code <= 95) {
                return String.fromCharCode(code - 64);
            }
        }

        if (key.length === 1 && altKey && !ctrlKey) {
            return "\x1b" + key;
        }

        return null;
    }

    // Wires the textarea to a terminal. `send` delivers data to the terminal program.
    function attach(terminal, container, send) {
        const cushion = " ".repeat(CUSHION);
        const box = document.createElement("textarea");
        box.className = "dam-touch-input";
        box.setAttribute("autocapitalize", "sentences");
        box.setAttribute("autocomplete", "off");
        box.setAttribute("enterkeyhint", "enter");
        box.setAttribute("aria-label", "Terminal input");
        box.rows = 1;
        container.appendChild(box);

        let enabled = false;
        let before = [];      // What the field held after the last change we handled, in graphemes.
        let cursor = 0;       // Where the terminal's cursor is, as a grapheme index in `before`.
        let resetPending = false;
        let writing = false;
        let composing = false;

        function write(text, caretAtEnd = true) {
            writing = true;
            box.value = text;
            if (caretAtEnd) {
                box.setSelectionRange(text.length, text.length);
            }
            writing = false;
            before = graphemes(text);
        }

        function reset() {
            resetPending = false;
            write(cushion);
            cursor = before.length;
        }

        function caretIndex() {
            // The caret as a grapheme index; the textarea counts UTF-16 units.
            return graphemes(box.value.slice(0, box.selectionEnd ?? box.value.length)).length;
        }

        function followCaret() {
            if (box.selectionStart !== box.selectionEnd) {
                return "";
            }

            const caret = caretIndex();
            const data = move(cursor, caret);
            cursor = caret;
            return data;
        }

        function handleChange() {
            if (writing || !enabled) {
                return;
            }

            let after = graphemes(box.value);
            let data = "";
            const newline = after.findIndex(g => g === "\n" || g === "\r" || g === "\r\n");
            let rest = null;
            if (newline >= 0) {
                // Enter typed as text (some keyboards do that instead of a key press): submit the line.
                rest = after.slice(newline + 1).join("");
                after = after.slice(0, newline);
            }

            const change = edit(before, after, cursor);
            data += change.data;
            before = after;
            cursor = change.cursor;

            if (newline >= 0) {
                send(data + "\r");
                reset();
                if (rest) {
                    // Text after the line break starts the next line.
                    send(rest);
                    write(cushion + rest);
                    cursor = before.length;
                }
                return;
            }

            data += followCaret();

            // Refill the cushion when Backspace ate into it, once the keyboard is done composing.
            const text = box.value;
            let spaces = 0;
            while (spaces < text.length && text[spaces] === " ") {
                spaces++;
            }
            if (spaces < CUSHION && !composing) {
                const start = box.selectionStart;
                const end = box.selectionEnd;
                const missing = CUSHION - spaces;
                write(" ".repeat(missing) + text, false);
                box.setSelectionRange(start + missing, end + missing);
                cursor += missing;
            }

            if (data) {
                send(data);
            }

            if (resetPending && !composing) {
                reset();
            }
        }

        box.addEventListener("compositionstart", () => composing = true);
        box.addEventListener("compositionend", () => {
            composing = false;
            // After the input event of the final composition; also empties the field when a reset is waiting.
            setTimeout(handleChange, 0);
        });

        box.addEventListener("input", handleChange);

        document.addEventListener("selectionchange", () => {
            // The caret was moved without typing (arrow keys, dragging over the space bar): move the terminal's cursor.
            if (!enabled || writing || document.activeElement !== box || box.value !== before.join("")) {
                return;
            }

            const data = followCaret();
            if (data) {
                send(data);
            }
        });

        box.addEventListener("keydown", event => {
            if (!enabled || event.keyCode === 229) {
                // The keyboard is editing text: handled from the input event.
                return;
            }

            const sequence = keySequence(event);
            if (sequence === null) {
                // Letters, Backspace, Delete, Left/Right, Home/End: the field handles them and the change is sent.
                return;
            }

            event.preventDefault();
            event.stopPropagation();
            send(sequence);
            if (composing || event.isComposing) {
                // Gboard sends Enter in the middle of composing a word. Emptying the field now would break the
                // keyboard's composition (and make it type the word again), so the text stays until the word is done.
                // Until then, changes are still sent as usual.
                before = graphemes(box.value);
                cursor = caretIndex();
                resetPending = true;
            } else {
                reset();
            }
        });

        // A paste goes through the terminal's own paste (bracketed when the program asks for it), so a multi-line text
        // stays one input instead of every line being submitted.
        box.addEventListener("paste", event => {
            if (!enabled) {
                return;
            }

            const text = event.clipboardData?.getData("text/plain");
            event.preventDefault();
            if (text) {
                terminal.paste(text.replace(/\r\n?/g, "\n"));
            }
            reset();
        });

        // The field sits inside the terminal's element, whose own handlers (xterm.js paste, keys) must not see these too.
        for (const type of ["keydown", "keyup", "keypress", "beforeinput", "input", "compositionstart", "compositionupdate",
            "compositionend", "paste", "copy", "cut"]) {
            box.addEventListener(type, event => {
                if (enabled) {
                    event.stopPropagation();
                }
            });
        }

        // Tapping the terminal focuses xterm.js's own textarea; hand the keyboard to ours instead.
        const xtermInput = terminal.textarea;
        xtermInput?.addEventListener("focus", () => {
            if (enabled) {
                box.focus({ preventScroll: true });
            }
        });

        return {
            get enabled() {
                return enabled;
            },

            setEnabled(value) {
                enabled = value;
                box.style.display = value ? "" : "none";
                terminal.options.cursorInactiveStyle = value ? "bar" : "outline";
                reset();
                if (value && document.activeElement === xtermInput) {
                    box.focus({ preventScroll: true });
                } else if (!value && document.activeElement === box) {
                    terminal.focus();
                }
            },

            // Call after sending keys that may change the line (the on-screen keys).
            reset,

            focus() {
                if (enabled) {
                    box.focus({ preventScroll: true });
                } else {
                    terminal.focus();
                }
            },
        };
    }

    const api = { attach, edit, graphemes, keySequence };
    if (typeof module !== "undefined" && module.exports) {
        module.exports = api;
    } else {
        root.DamTouchInput = api;
    }
})(typeof window !== "undefined" ? window : globalThis);
