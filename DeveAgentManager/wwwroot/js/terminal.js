// Browser side of the Portal's terminals (xterm.js through XtermBlazor) and small UI helpers.
(function () {
    // Addons every terminal can use; XtermBlazor clones them per terminal.
    XtermBlazor.registerAddons({
        "addon-fit": new FitAddon.FitAddon(),
        "addon-web-links": new WebLinksAddon.WebLinksAddon((event, uri) => window.open(uri, "_blank", "noopener")),
    });

    const observers = new Map();

    function fit(id) {
        try {
            const addon = XtermBlazor.getTerminalObjectById(id).addons.get("addon-fit");
            addon.fit();
        } catch {
            // The terminal is not rendered (yet) or was disposed.
        }
    }

    window.damTerminal = {
        init(id) {
            const terminal = XtermBlazor.getTerminalById(id);

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
        },

        fit,

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
