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

    function fit(id) {
        try {
            const addon = XtermBlazor.getTerminalObjectById(id).addons.get("addon-fit");
            addon.fit();
        } catch {
            // The terminal is not rendered (yet) or was disposed.
        }
    }

    const isTouch = () => window.matchMedia("(pointer: coarse)").matches || navigator.maxTouchPoints > 0;

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

    function getTerminal(id) {
        try {
            return XtermBlazor.getTerminalById(id);
        } catch {
            return null;
        }
    }

    window.damTerminal = {
        // Returns whether this is a touch device, which gets the text box for typing.
        init(id) {
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

                // Ctrl+C copies when text is selected (select with Shift+drag); otherwise it interrupts as usual.
                if (event.type === "keydown" && event.ctrlKey && !event.shiftKey && event.key === "c" && terminal.hasSelection()) {
                    navigator.clipboard?.writeText(terminal.getSelection());
                    terminal.clearSelection();
                    return false;
                }

                return true;
            });
            terminal.attachCustomWheelEventHandler(() => true);

            const element = document.getElementById(id);
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
                observer.observe(host);
                observers.set(id, observer);
            }

            fit(id);
            return isTouch();
        },

        fit,

        // Sets up the text box that phones type into. Enter adds a new line, Ctrl/Cmd+Enter sends.
        initCompose(id, box, focus) {
            if (!box) {
                return;
            }

            if (!box.dataset.wired) {
                box.dataset.wired = "true";
                const grow = () => {
                    box.style.height = "auto";
                    box.style.height = `${Math.min(box.scrollHeight, 160)}px`;
                };
                box.addEventListener("input", grow);
                box.addEventListener("keydown", event => {
                    if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
                        event.preventDefault();
                        this.sendCompose(id, box, true);
                    }
                });
                grow();
            }

            if (focus) {
                box.focus({ preventScroll: true });
            }
        },

        // Sends the text box's text as one paste (bracketed when the program asks for it, so a multi-line text stays
        // one input instead of every line being submitted), then Enter when submitting.
        sendCompose(id, box, submit) {
            const terminal = getTerminal(id);
            if (!terminal || !box) {
                return;
            }

            const text = box.value.replace(/\r\n?/g, "\n");
            if (text.length > 0) {
                terminal.paste(text);
            }

            if (submit) {
                // After the paste, so the program sees the whole text before Enter.
                terminal.input("\r", true);
            }

            box.value = "";
            box.dispatchEvent(new Event("input"));
            box.focus({ preventScroll: true });
        },

        dispose(id) {
            observers.get(id)?.disconnect();
            observers.delete(id);
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
