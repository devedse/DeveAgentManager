// Browser side of the Portal's terminals (xterm.js through XtermBlazor) and small UI helpers.
(function () {
    // Addons every terminal can use; XtermBlazor clones them per terminal.
    XtermBlazor.registerAddons({
        "addon-fit": new FitAddon.FitAddon(),
        "addon-web-links": new WebLinksAddon.WebLinksAddon((event, uri) => window.open(uri, "_blank", "noopener")),
    });

    const observers = new Map();

    // Tracks the visible part of the screen. A phone's on-screen keyboard covers the page without resizing it
    // (iOS Safari, and Chrome on Android by default), so the full-screen terminal page sizes itself to this instead.
    (function trackVisualViewport() {
        const viewport = window.visualViewport;
        if (!viewport) {
            return;
        }

        const root = document.documentElement;
        let frame = 0;
        const update = () => {
            cancelAnimationFrame(frame);
            frame = requestAnimationFrame(() => {
                root.style.setProperty("--dam-vv-height", `${Math.round(viewport.height)}px`);
                root.style.setProperty("--dam-vv-top", `${Math.round(viewport.offsetTop)}px`);
            });
        };
        viewport.addEventListener("resize", update);
        viewport.addEventListener("scroll", update);
        update();
    })();

    // On touch devices the terminal keeps its rows when the frame only gets shorter, which is the on-screen keyboard
    // opening. Fewer rows would resize tmux's pane, and pi then redraws its whole screen, so the history streams by every
    // time the keyboard opens or closes. Instead the terminal keeps its full height in the (clipped) frame, positioned
    // so the cursor stays in view. A change in width (rotating) or a taller frame fits the terminal again.
    const layouts = new Map();

    function fit(id) {
        try {
            const terminal = XtermBlazor.getTerminalById(id);
            const addon = XtermBlazor.getTerminalObjectById(id).addons.get("addon-fit");
            const host = terminal.element?.closest(".terminal-host");
            const frame = host?.parentElement;
            if (!host || !frame || !isTouch()) {
                addon.fit();
                return;
            }

            const width = frame.clientWidth;
            const height = frame.clientHeight;
            const layout = layouts.get(id);
            if (!layout || layout.width !== width || height >= layout.height) {
                layouts.set(id, { width, height, offset: 0 });
                host.style.height = "";
                host.style.top = "";
                addon.fit();
                return;
            }

            host.style.height = `${layout.height}px`;
            followCursor(id, true);
        } catch {
            // The terminal is not rendered (yet) or was disposed.
        }
    }

    // While the frame is shorter than the terminal: shift the terminal so the cursor (pi's prompt) is visible, with a
    // little room below it. It only moves when the cursor leaves the visible part, so output doesn't make it jump.
    function followCursor(id, force = false) {
        const layout = layouts.get(id);
        const terminal = getTerminal(id);
        const host = terminal?.element?.closest(".terminal-host");
        const frame = host?.parentElement;
        if (!layout || !host || !frame || !host.style.height) {
            return;
        }

        const visible = frame.clientHeight;
        const hidden = Math.max(0, layout.height - visible);
        const screen = terminal.element.querySelector(".xterm-screen");
        const rowHeight = (screen?.clientHeight ?? 0) / Math.max(1, terminal.rows);
        const top = (screen?.getBoundingClientRect().top ?? 0) - host.getBoundingClientRect().top;
        const cursorTop = top + terminal.buffer.active.cursorY * rowHeight;
        const cursorBottom = cursorTop + rowHeight;

        const shown = cursorTop >= layout.offset && cursorBottom <= layout.offset + visible;
        if (force || !shown) {
            // As low as possible (the latest output), but with the cursor and two rows below it in view.
            layout.offset = Math.min(hidden, Math.max(0, cursorBottom + 2 * rowHeight - visible));
        }

        layout.offset = Math.min(layout.offset, hidden);
        host.style.top = `${-layout.offset}px`;
        // Focusing or typing can scroll a clipped frame on its own; the position above is the only scrolling.
        frame.scrollTop = 0;
    }

    // Phones and tablets: the primary pointer is a finger.
    const isTouch = () => window.matchMedia("(pointer: coarse)").matches;

    // Per terminal: the phone keyboard input (terminal-touch-input.js).
    const touchInputs = new Map();
    const TOUCH_INPUT_KEY = "dam.touchInput";

    // On by default on touch devices; the "⌨" key switches it, and the choice is remembered in this browser.
    function touchInputWanted() {
        const stored = localStorage.getItem(TOUCH_INPUT_KEY);
        return stored === null ? isTouch() : stored === "on";
    }

    // Phone keyboards rewrite text they already typed (autocorrect, predictions, swipe typing). xterm.js has already
    // sent those keys, so the terminal ends up with garbled text. Ask the browser not to do that in xterm's input.
    function hardenInput(terminal) {
        const textarea = terminal.textarea;
        if (!textarea) {
            return;
        }

        textarea.setAttribute("autocomplete", "off");
        textarea.setAttribute("autocorrect", "off");
        textarea.setAttribute("autocapitalize", "off");
        textarea.setAttribute("spellcheck", "false");
        textarea.setAttribute("enterkeyhint", "send");
    }

    // Swipe to scroll on touch screens. xterm.js only scrolls its own scrollback on touch. When the program asks for
    // mouse events, like tmux with "mouse on", a swipe does nothing, because the scrollback lives in tmux. Turn the swipe
    // into wheel events, which xterm.js reports to tmux, so tmux scrolls (and enters copy mode) as with a mouse wheel.
    function enableTouchScroll(terminal, element) {
        const screen = element.querySelector(".xterm-screen") ?? element;
        let lastY = null;
        let lastX = 0;
        let pending = 0;
        let velocity = 0;
        let lastTime = 0;
        let momentum = 0;

        const mouseMode = () => terminal.modes.mouseTrackingMode !== "none";
        const rowHeight = () => screen.clientHeight / Math.max(1, terminal.rows);

        // tmux scrolls several lines per wheel step, so send one step for every few rows the finger moves.
        const stepPixels = () => rowHeight() * 2;

        function wheel(steps, x, y) {
            for (let i = 0; i < Math.abs(steps); i++) {
                screen.dispatchEvent(new WheelEvent("wheel", {
                    deltaY: steps > 0 ? 1 : -1,
                    deltaMode: WheelEvent.DOM_DELTA_LINE,
                    clientX: x,
                    clientY: y,
                    bubbles: true,
                    cancelable: true,
                }));
            }
        }

        function scrollBy(pixels, x, y) {
            pending += pixels;
            const step = stepPixels();
            const steps = Math.trunc(pending / step);
            if (steps !== 0) {
                pending -= steps * step;
                wheel(steps, x, y);
            }
        }

        element.addEventListener("touchstart", event => {
            cancelAnimationFrame(momentum);
            if (event.touches.length !== 1 || !mouseMode()) {
                lastY = null;
                return;
            }

            lastY = event.touches[0].clientY;
            lastX = event.touches[0].clientX;
            pending = 0;
            velocity = 0;
            lastTime = event.timeStamp;
        }, { capture: true, passive: true });

        element.addEventListener("touchmove", event => {
            if (lastY === null || event.touches.length !== 1 || !mouseMode()) {
                return;
            }

            const touch = event.touches[0];
            const delta = lastY - touch.clientY;
            const elapsed = Math.max(1, event.timeStamp - lastTime);
            velocity = 0.8 * (delta / elapsed) + 0.2 * velocity;
            lastY = touch.clientY;
            lastX = touch.clientX;
            lastTime = event.timeStamp;
            scrollBy(delta, lastX, lastY);

            // Keep the page and xterm's own touch handling from also acting on this swipe.
            event.preventDefault();
            event.stopPropagation();
        }, { capture: true, passive: false });

        element.addEventListener("touchend", () => {
            if (lastY === null) {
                return;
            }

            // A short glide after a flick, like native scrolling.
            const x = lastX;
            const y = lastY;
            lastY = null;
            let speed = velocity;
            let previous = performance.now();
            const glide = now => {
                const elapsed = now - previous;
                previous = now;
                speed *= Math.pow(0.95, elapsed / 16);
                if (Math.abs(speed) < 0.05 || !mouseMode()) {
                    return;
                }

                scrollBy(speed * elapsed, x, y);
                momentum = requestAnimationFrame(glide);
            };
            if (Math.abs(velocity) > 0.3) {
                momentum = requestAnimationFrame(glide);
            }
        }, { capture: true, passive: true });
    }

    // Images into the terminal: pasted (Ctrl+V), dropped, or picked with the "📎 Image" key (phones: gallery, camera,
    // screenshots). The Manager stores them on the Runner and returns the path, which is pasted into the terminal as
    // text, like pi's own Ctrl+V does with an image from the local clipboard. The harness sends the file to the model.
    const IMAGE_TYPES = ["image/png", "image/jpeg", "image/webp", "image/gif"];

    function imageFiles(dataTransfer) {
        return Array.from(dataTransfer?.files ?? []).filter(file => file.type.startsWith("image/"));
    }

    async function sendImages(terminal, dotnet, files, afterPaste) {
        const paths = [];
        for (const file of files) {
            // Unsupported types are refused by the Manager, which says which types work.
            const path = await dotnet.invokeMethodAsync("UploadImageAsync", file.name || "", file.type, file.size, DotNet.createJSStreamReference(file));
            if (path) {
                paths.push(path);
            }
        }

        if (paths.length > 0) {
            afterPaste();
            // A space after each, so text typed next doesn't run into the path.
            terminal.paste(paths.join(" ") + " ");
            terminal.focus();
        }
    }

    function enableImagePaste(terminal, element, dotnet, afterPaste) {
        // Capture phase: before xterm.js (and the phone keyboard input) handle the paste as text.
        element.addEventListener("paste", event => {
            const files = imageFiles(event.clipboardData);
            // Text wins when there is some: copying from a spreadsheet or a web page puts a picture next to it too.
            const text = event.clipboardData?.getData("text/plain") ?? "";
            if (files.length === 0 || text.trim().length > 0) {
                return;
            }

            event.preventDefault();
            event.stopPropagation();
            sendImages(terminal, dotnet, files, afterPaste);
        }, { capture: true });

        element.addEventListener("dragover", event => {
            if (Array.from(event.dataTransfer?.items ?? []).some(item => item.kind === "file")) {
                event.preventDefault();
                event.dataTransfer.dropEffect = "copy";
            }
        });

        element.addEventListener("drop", event => {
            const files = imageFiles(event.dataTransfer);
            if (files.length === 0) {
                return;
            }

            event.preventDefault();
            sendImages(terminal, dotnet, files, afterPaste);
        });

        return () => {
            const picker = document.createElement("input");
            picker.type = "file";
            picker.accept = IMAGE_TYPES.join(",");
            picker.multiple = true;
            // In the page while it is open: some browsers (iOS Safari) don't open a picker for a detached input.
            picker.style.display = "none";
            document.body.appendChild(picker);
            const done = () => picker.remove();
            picker.addEventListener("change", () => {
                const files = Array.from(picker.files ?? []);
                done();
                sendImages(terminal, dotnet, files, afterPaste);
            });
            picker.addEventListener("cancel", done);
            picker.click();
        };
    }

    function getTerminal(id) {
        try {
            return XtermBlazor.getTerminalById(id);
        } catch {
            return null;
        }
    }

    window.damTerminal = {
        // Returns whether the phone keyboard input is on.
        init(id, dotnet) {
            const terminal = XtermBlazor.getTerminalById(id);
            hardenInput(terminal);

            // Handle keys in the browser instead of asking .NET for every key press.
            terminal.attachCustomKeyEventHandler(event => {
                // xterm.js sends a plain Enter for Shift+Enter. Send the CSI-u sequence instead, which tmux forwards
                // (extended-keys) so harnesses like pi can insert a new line.
                if (event.key === "Enter" && event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
                    if (event.type === "keydown") {
                        terminal.input("\x1b[13;2u", true);
                    }
                    return false;
                }

                // Ctrl+V (Ctrl+Shift+V, Shift+Insert) pastes from this computer's clipboard. xterm.js would send ^V instead,
                // which asks the program to read the clipboard of the Runner, which has none. Returning false leaves the key
                // to the browser, which pastes: text through xterm.js, images through enableImagePaste.
                const key = event.key.toLowerCase();
                if ((event.ctrlKey && !event.altKey && !event.metaKey && key === "v") || (event.shiftKey && event.key === "Insert")) {
                    return false;
                }

                // Ctrl+C copies when text is selected (select with Shift+drag); otherwise it interrupts as usual.
                if (event.type === "keydown" && event.ctrlKey && !event.shiftKey && event.key === "c" && terminal.hasSelection()) {
                    navigator.clipboard?.writeText(terminal.getSelection());
                    terminal.clearSelection();
                    return false;
                }

                return true;
            });
            terminal.attachCustomWheelEventHandler(() => true);

            // XtermBlazor's id is Blazor's element reference, not an HTML id: find the element through the terminal.
            const element = terminal.element?.parentElement ?? document.getElementById(id);
            const xtermElement = terminal.element ?? element;
            if (xtermElement && !xtermElement.dataset.touchScroll) {
                xtermElement.dataset.touchScroll = "true";
                enableTouchScroll(terminal, xtermElement);
            }

            const host = element?.closest(".terminal-host") ?? element?.parentElement;
            if (host && !observers.has(id)) {
                let frame = 0;
                const observer = new ResizeObserver(() => {
                    cancelAnimationFrame(frame);
                    frame = requestAnimationFrame(() => fit(id));
                });
                // The frame: on touch devices the host keeps its height while the keyboard is open.
                observer.observe(host.parentElement ?? host);
                observers.set(id, observer);

                let cursorFrame = 0;
                terminal.onCursorMove(() => {
                    cancelAnimationFrame(cursorFrame);
                    cursorFrame = requestAnimationFrame(() => followCursor(id));
                });
            }

            if (xtermElement && !touchInputs.has(id)) {
                const input = DamTouchInput.attach(terminal, xtermElement, data => terminal.input(data, true));
                input.setEnabled(touchInputWanted());
                touchInputs.set(id, input);
            }

            let pickImage = null;
            if (xtermElement && dotnet && !xtermElement.dataset.imagePaste) {
                xtermElement.dataset.imagePaste = "true";
                pickImage = enableImagePaste(terminal, xtermElement, dotnet, () => touchInputs.get(id)?.reset());
            }

            // The on-screen keys must not take the focus: that would close the phone keyboard after every key.
            const keys = element?.closest(".terminal-frame")?.parentElement?.querySelector(".terminal-keys");
            if (keys && !keys.dataset.keepFocus) {
                keys.dataset.keepFocus = "true";
                keys.addEventListener("pointerdown", event => {
                    if (event.target.closest("button")) {
                        event.preventDefault();
                    }
                });
                keys.querySelector('[data-dam-action="image"]')?.addEventListener("click", () => pickImage?.());
            }

            fit(id);
            return touchInputs.get(id)?.enabled ?? false;
        },

        fit,

        // Sends an on-screen key. The phone keyboard input forgets its line, which the key may have changed.
        sendKey(id, sequence) {
            const terminal = getTerminal(id);
            if (!terminal) {
                return;
            }

            const input = touchInputs.get(id);
            input?.reset();
            terminal.input(sequence, true);
        },

        // Switches the phone keyboard input on or off and returns the new state.
        toggleTouchInput(id) {
            const input = touchInputs.get(id);
            if (!input) {
                return false;
            }

            const enabled = !input.enabled;
            localStorage.setItem(TOUCH_INPUT_KEY, enabled ? "on" : "off");
            input.setEnabled(enabled);
            input.focus();
            return enabled;
        },

        focus(id) {
            const input = touchInputs.get(id);
            if (input) {
                input.focus();
            } else {
                getTerminal(id)?.focus();
            }
        },

        dispose(id) {
            observers.get(id)?.disconnect();
            observers.delete(id);
            touchInputs.delete(id);
            layouts.delete(id);
        },
    };

    window.damUi = {
        // Keeps a log scrolled to the bottom, unless the user scrolled up to read.
        stickToBottom(element, force) {
            if (!element) {
                return;
            }

            const nearBottom = element.scrollHeight - element.scrollTop - element.clientHeight < 80;
            if (force || nearBottom || element.dataset.stuck !== "false") {
                element.scrollTop = element.scrollHeight;
            }

            if (!element.dataset.watching) {
                element.dataset.watching = "true";
                element.addEventListener("scroll", () => {
                    element.dataset.stuck = (element.scrollHeight - element.scrollTop - element.clientHeight < 80).toString();
                });
            }
        },

        async copy(text) {
            await navigator.clipboard.writeText(text);
        },

        async download(fileName, contentType, streamReference) {
            const buffer = await streamReference.arrayBuffer();
            const url = URL.createObjectURL(new Blob([buffer], { type: contentType }));
            const link = document.createElement("a");
            link.href = url;
            link.download = fileName;
            link.click();
            URL.revokeObjectURL(url);
        },

        scrollPageToEnd() {
            window.scrollTo(0, document.body.scrollHeight);
        },
    };
})();
