// Keeps the chosen and the focused tab of every tab bar in view. The bars scroll natively (app.css), and the browser
// shows a clicked tab, but MudTabs moves the keyboard focus without scrolling, and the page can choose a tab itself,
// such as for a link to ?tab=diagnostics (MudTabs renders the first tab chosen, then the linked one).
(() => {
    const reveal = (tab: HTMLElement) => {
        const bar = tab.closest('.mud-tabs-tabbar-content');
        if (!(bar instanceof HTMLElement)) return;
        const box = tab.getBoundingClientRect(), view = bar.getBoundingClientRect();
        if (box.left < view.left) bar.scrollLeft -= view.left - box.left;
        else if (box.right > view.right) bar.scrollLeft += Math.min(box.right - view.right, box.left - view.left);
    };

    // Only aria-selected changes are reported, so busy pages (a terminal) cost nothing.
    new MutationObserver(records => {
        for (const record of records) {
            if (record.target instanceof HTMLElement && record.target.getAttribute('aria-selected') === 'true'
                && record.target.classList.contains('mud-tab')) reveal(record.target);
        }
    }).observe(document.documentElement, { subtree: true, attributes: true, attributeFilter: ['aria-selected'] });
    document.addEventListener('focusin', event => {
        if (event.target instanceof HTMLElement && event.target.matches('.mud-tabs-tabbar .mud-tab')) reveal(event.target);
    });
})();

