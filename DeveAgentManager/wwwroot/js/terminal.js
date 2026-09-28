// Browser side of the Portal's terminals (xterm.js through XtermBlazor) and small UI helpers.
(function () {
    // Addons every terminal can use; XtermBlazor clones them per terminal.
    XtermBlazor.registerAddons({
        "addon-fit": new FitAddon.FitAddon(),
        "addon-web-links": new WebLinksAddon.WebLinksAddon((event, uri) => window.open(uri, "_blank", "noopener")),
    });

    const observers = new Map();

    // Where the on-screen keyboard only covers the page (iOS Safari), the full-screen terminal page sizes itself to the
    // visible part. Browsers that honour interactive-widget=resizes-content (Chrome, Firefox on Android) shrink the
    // page itself, so 100dvh is right there; a height set from here could be read mid-animation and stay too short
    // after the keyboard closes (an empty strip under the key row).
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
                const covered = window.innerHeight - viewport.height > 1 || viewport.offsetTop > 1;
                if (covered) {
                    root.style.setProperty("--dam-vv-height", `${Math.round(viewport.height)}px`);
                    root.style.setProperty("--dam-vv-top", `${Math.round(viewport.offsetTop)}px`);
                } else {
                    root.style.removeProperty("--dam-vv-height");
                    root.style.removeProperty("--dam-vv-top");
                }
            });
        };
        viewport.addEventListener("resize", update);
        viewport.addEventListener("scroll", update);
        window.addEventListener("resize", update);
        // The keyboard closes after the text box lost focus; check again once it has.
        document.addEventListener("focusout", () => setTimeout(update, 400));
        update();
    })();

    // A page with ?viewport in its address shows how the screen is measured, to tell where unused space comes from.
    (function viewportReadout() {
        if (!new URLSearchParams(location.search).has("viewport")) {
            return;
        }

        const box = document.createElement("div");
        box.style.cssText = "position:fixed;top:60px;right:8px;z-index:100000;background:rgba(0,0,0,.85);color:#7fffd4;" +
            "font:12px/1.35 monospace;padding:8px 10px;border-radius:8px;white-space:pre;pointer-events:none";
        const probe = document.createElement("div");
        probe.style.cssText = "position:fixed;left:-9999px;top:0;width:1px;visibility:hidden;padding-bottom:env(safe-area-inset-bottom)";
        const unit = value => {
            probe.style.height = value;
            return Math.round(probe.getBoundingClientRect().height);
        };
        const bottom = selector => {
            const element = document.querySelector(selector);
            return element ? Math.round(element.getBoundingClientRect().bottom) : "-";
        };
        const show = () => {
            const vv = window.visualViewport;
            probe.style.height = "0px";
            const inset = Math.round(probe.getBoundingClientRect().height);
            const keys = document.querySelector(".terminal-keys");
            box.textContent = [
                `innerHeight      ${window.innerHeight}`,
                `clientHeight     ${document.documentElement.clientHeight}`,
                `visualViewport   ${vv ? `${Math.round(vv.height)} (top ${Math.round(vv.offsetTop)})` : "-"}`,
                `100dvh/svh/lvh   ${unit("100dvh")}/${unit("100svh")}/${unit("100lvh")}`,
                `screen/avail     ${screen.height}/${screen.availHeight} @${window.devicePixelRatio}`,
                `safe-area bottom ${inset}`,
                `--dam-vv-height  ${document.documentElement.style.getPropertyValue("--dam-vv-height") || "(none)"}`,
                `layout bottom    ${bottom(".terminal-layout")}`,
                `keys bottom      ${bottom(".terminal-keys")} (padding ${keys ? getComputedStyle(keys).paddingBottom : "-"})`,
            ].join("\n");
        };
        document.body.append(box, probe);
        window.visualViewport?.addEventListener("resize", show);
        window.addEventListener("resize", show);
        setInterval(show, 1000);
        show();
    })();

    // On touch devices the terminal keeps its rows when the frame only gets shorter, which is the on-screen keyboard
    // opening, or a little taller (the address bar hiding). Fewer rows would resize tmux's pane, and pi then redraws its whole screen, so the history streams by every
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
            const screen = terminal.element.querySelector(".xterm-screen");
            const rowHeight = (screen?.clientHeight ?? 0) / Math.max(1, terminal.rows) || 16;
            // Growing by a few rows is the browser's address bar hiding (or the keyboard's suggestion strip): not worth
            // a resize, which makes pi redraw everything. The terminal keeps its rows and a strip stays unused.
            const grewALot = layout && height >= layout.height + 4 * rowHeight;
            if (!layout || layout.width !== width || grewALot) {
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

    // Per terminal: its WebSocket to the Manager, and the .NET component that shows its state.
    const sockets = new Map();
    const dotnets = new Map();

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

    const MAX_IMAGE_BYTES = 25 * 1024 * 1024; // TerminalService.MaxImageBytes
    const IMAGE_CHUNK_BYTES = 64 * 1024; // TerminalSocket.ImageChunkBytes
    let nextImageId = 1;

    // Sends an image over the terminal's socket (TerminalSocket.Image); resolves to its path on the Runner.
    // Not an HTTP upload: a reverse proxy's request size limit (nginx: 1 MB by default) doesn't apply to a WebSocket.
    // When the connection drops meanwhile (a bad network), it is sent again once the terminal has reconnected.
    async function uploadImage(id, file) {
        if (file.size > MAX_IMAGE_BYTES) {
            throw new Error(`Images can be up to ${MAX_IMAGE_BYTES / 1024 / 1024} MB.`);
        }

        const bytes = new Uint8Array(await file.arrayBuffer());
        if (bytes.length === 0) {
            throw new Error("The image is empty.");
        }

        for (let attempt = 1; ; attempt++) {
            const connection = attempt === 1 ? openConnection(id) : await reconnected(id, 30000);
            if (!connection) {
                throw new Error(attempt === 1 ? "The terminal isn't connected." : "The connection dropped and didn't come back. Paste the image again.");
            }

            try {
                return await sendImage(connection, file.type, bytes);
            } catch (error) {
                if (!error.dropped || attempt === 3) {
                    throw error;
                }
            }
        }
    }

    function openConnection(id) {
        const connection = sockets.get(id);
        return connection?.ready && connection.socket.readyState === WebSocket.OPEN ? connection : null;
    }

    // The terminal's next connection, once it is attached (the terminal reconnects by itself after a drop).
    async function reconnected(id, timeout) {
        const until = Date.now() + timeout;
        while (Date.now() < until) {
            const connection = openConnection(id);
            if (connection) {
                return connection;
            }

            await new Promise(resolve => setTimeout(resolve, 250));
        }

        return null;
    }

    function sendImage(connection, contentType, bytes) {
        return new Promise((resolve, reject) => {
            const imageId = nextImageId++;
            connection.images.set(imageId, { resolve, reject });
            // Header and bytes in one go, without awaiting: nothing typed meanwhile can end up between them.
            connection.socket.send(JSON.stringify({ type: "image", id: imageId, contentType, size: bytes.length }));
            for (let offset = 0; offset < bytes.length; offset += IMAGE_CHUNK_BYTES) {
                connection.socket.send(bytes.subarray(offset, offset + IMAGE_CHUNK_BYTES));
            }
        });
    }

    // Uploads images to the terminal's Runner (the Manager stores them) and pastes their paths into the terminal.
    // Progress and errors go to .NET (snackbars).
    async function sendImages(terminal, dotnet, id, files, afterPaste) {
        const paths = [];
        for (const file of files) {
            dotnet?.invokeMethodAsync("OnImageStatus", "uploading", `${file.name || "image"} (${Math.max(1, Math.round(file.size / 1024))} KB)`);
            try {
                paths.push(await uploadImage(id, file));
            } catch (error) {
                dotnet?.invokeMethodAsync("OnImageStatus", "error", error?.message ?? String(error));
            }
        }

        dotnet?.invokeMethodAsync("OnImageStatus", "done", "");
        if (paths.length > 0) {
            afterPaste();
            // A space after each, so text typed next doesn't run into the path.
            terminal.paste(paths.join(" ") + " ");
            terminal.focus();
        }
    }

    function enableImagePaste(terminal, element, dotnet, id, afterPaste) {
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
            sendImages(terminal, dotnet, id, files, afterPaste);
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
            sendImages(terminal, dotnet, id, files, afterPaste);
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
                sendImages(terminal, dotnet, id, files, afterPaste);
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
        // Returns whether the phone keyboard input is on. `images`: whether images can be pasted (they go to the Runner).
        init(id, dotnet, images) {
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
            dotnets.set(id, dotnet);
            if (xtermElement && dotnet && images && !xtermElement.dataset.imagePaste) {
                xtermElement.dataset.imagePaste = "true";
                pickImage = enableImagePaste(terminal, xtermElement, dotnet, id, () => touchInputs.get(id)?.reset());
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

        // Resolves once the page is visible (right away when it already is).
        whenVisible() {
            if (document.visibilityState === "visible") {
                return Promise.resolve();
            }

            return new Promise(resolve => {
                const check = () => {
                    if (document.visibilityState === "visible") {
                        document.removeEventListener("visibilitychange", check);
                        resolve();
                    }
                };
                document.addEventListener("visibilitychange", check);
            });
        },

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

        // Connects the terminal to the Manager's terminal WebSocket: binary frames are the terminal's data both ways,
        // text frames are JSON control messages (ready, closed, login; resize). Tells .NET when it is ready or closed.
        connect(id, url) {
            this.disconnect(id);
            const terminal = getTerminal(id);
            const dotnet = dotnets.get(id);
            if (!terminal) {
                return;
            }

            const address = new URL(`${url}&cols=${terminal.cols}&rows=${terminal.rows}`, document.baseURI);
            address.protocol = address.protocol === "https:" ? "wss:" : "ws:";
            const socket = new WebSocket(address.href);
            socket.binaryType = "arraybuffer";
            const encoder = new TextEncoder();
            const connection = { socket, subscriptions: [], closed: null, images: new Map() };
            const send = data => {
                if (socket.readyState === WebSocket.OPEN) {
                    socket.send(data);
                }
            };

            socket.onmessage = event => {
                if (typeof event.data !== "string") {
                    terminal.write(new Uint8Array(event.data));
                    return;
                }

                const message = JSON.parse(event.data);
                if (message.type === "ready") {
                    connection.ready = true;
                    dotnet?.invokeMethodAsync("OnSocketReady", message.tty ?? "");
                } else if (message.type === "closed") {
                    connection.closed = message;
                } else if (message.type === "login") {
                    dotnet?.invokeMethodAsync("OnLoginResult", !!message.ok, message.message ?? "");
                } else if (message.type === "image") {
                    const waiting = connection.images.get(message.id);
                    connection.images.delete(message.id);
                    message.error ? waiting?.reject(new Error(message.error)) : waiting?.resolve(message.path);
                }
            };
            socket.onclose = () => {
                connection.subscriptions.forEach(s => s.dispose());
                connection.images.forEach(waiting => waiting.reject(Object.assign(new Error("The connection dropped before the image was stored. Paste it again."), { dropped: true })));
                connection.images.clear();
                // Only the current connection reports: an old one closing after a reconnect says nothing.
                if (sockets.get(id) === connection) {
                    sockets.delete(id);
                    dotnet?.invokeMethodAsync("OnSocketClosed", connection.closed?.reason ?? null, connection.closed?.message ?? null);
                }
            };

            connection.subscriptions.push(terminal.onData(data => send(encoder.encode(data))));
            connection.subscriptions.push(terminal.onBinary(data => send(Uint8Array.from(data, c => c.charCodeAt(0) & 255))));
            connection.subscriptions.push(terminal.onResize(size => send(JSON.stringify({ type: "resize", cols: size.cols, rows: size.rows }))));
            sockets.set(id, connection);
        },

        disconnect(id) {
            const connection = sockets.get(id);
            if (connection) {
                sockets.delete(id);
                connection.subscriptions.forEach(s => s.dispose());
                connection.socket.close();
            }
        },

        dispose(id) {
            this.disconnect(id);
            observers.get(id)?.disconnect();
            observers.delete(id);
            touchInputs.delete(id);
            layouts.delete(id);
            dotnets.delete(id);
        },
    };

    // Light or dark (Settings → Appearance), per browser. "system" follows the device, also when it changes.
    const THEME_KEY = "dam-theme";
    const themeWatchers = new Set();
    const systemLight = window.matchMedia?.("(prefers-color-scheme: light)");

    function themeMode() {
        try {
            return localStorage.getItem(THEME_KEY) || "system";
        } catch {
            return "system";
        }
    }

    function applyTheme() {
        const mode = themeMode();
        const dark = mode === "dark" || (mode !== "light" && !systemLight?.matches);
        document.documentElement.dataset.theme = dark ? "dark" : "light";
        const meta = document.querySelector('meta[name="theme-color"]');
        if (meta) {
            meta.content = dark ? "#0d1017" : "#f5f6fa";
        }

        themeWatchers.forEach(watcher => watcher.invokeMethodAsync("OnThemeChanged", dark).catch(() => themeWatchers.delete(watcher)));
        return dark;
    }

    systemLight?.addEventListener("change", () => {
        if (themeMode() === "system") {
            applyTheme();
        }
    });

    window.damTheme = {
        // Returns [mode, isDark].
        get() {
            return [themeMode(), document.documentElement.dataset.theme !== "light"];
        },

        set(mode) {
            try {
                localStorage.setItem(THEME_KEY, mode);
            } catch {
                // Private window: it lasts for this page.
            }

            return applyTheme();
        },

        watch(dotnet) {
            themeWatchers.add(dotnet);
        },

        unwatch(dotnet) {
            themeWatchers.delete(dotnet);
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

        // Downloads a file the Manager serves (e.g. a session export); the login cookie goes along.
        downloadUrl(url) {
            const link = document.createElement("a");
            link.href = url;
            link.download = "";
            document.body.appendChild(link);
            link.click();
            link.remove();
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

        // The Portal runs: remove the loading screen the Manager sent with the page.
        hideLoading() {
            document.getElementById("app-loading")?.remove();
        },

        scrollPageToEnd() {
            window.scrollTo(0, document.body.scrollHeight);
        },

        // Whether the browser can reach a URL on another origin (e.g. the Manager's VS Code port). The answer can't be
        // read across origins, but a request that fails at all means the port isn't reachable.
        async canReach(url) {
            try {
                await fetch(url, { mode: "no-cors", credentials: "include", cache: "no-store" });
                return true;
            } catch {
                return false;
            }
        },

        // Full screen for an element (e.g. the VS Code frame); false where the browser doesn't allow it (iPhone).
        async fullscreen(element) {
            if (!element?.requestFullscreen) {
                return false;
            }

            try {
                await element.requestFullscreen();
                return true;
            } catch {
                return false;
            }
        },
    };
})();
