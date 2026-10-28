// Keeps a hold-to-confirm bubble (HoldButton) in view: shifted sideways where it would run off the page's content area
// (under the navigation drawer or off the screen), and below the button when there is no room above it.
export function place(root: HTMLElement) {
    const bubble = root?.querySelector<HTMLElement>('.hold-bubble');
    if (!bubble) return;
    const margin = 8;
    bubble.classList.remove('hold-bubble-below');
    bubble.style.setProperty('--hold-shift', '0px');
    const rect = bubble.getBoundingClientRect();
    const area = root.closest('.mud-main-content')?.getBoundingClientRect();
    const left = Math.max(area?.left ?? 0, 0) + margin;
    const right = Math.min(area?.right ?? Infinity, document.documentElement.clientWidth) - margin;
    if (rect.top < Math.max(area?.top ?? 0, 0) + margin) bubble.classList.add('hold-bubble-below');
    const shift = rect.left < left ? left - rect.left : rect.right > right ? right - rect.right : 0;
    bubble.style.setProperty('--hold-shift', `${Math.round(shift)}px`);
}

// Rich upgrade help has a fixed width so it cannot oscillate while the popover measures it near an edge.
// Preserve MudBlazor's vertical flip; shift only the part that would extend beyond the viewport.
export function placeHelp(id: string): void {
    let attempts = 0;
    const position = () => {
        const popover = document.querySelector<HTMLElement>(`[data-hold-help="${id}"]`)?.closest<HTMLElement>('.mud-popover');
        if (!popover) {
            if (++attempts < 30) requestAnimationFrame(position);
            return;
        }
        popover.style.translate = 'none';
        const rect = popover.getBoundingClientRect();
        // A portal can exist at its off-screen measurement position before MudBlazor anchors it.
        if (!popover.classList.contains('mud-popover-open') || rect.width === 0 || rect.left < -1000
            || rect.left > document.documentElement.clientWidth + 1000) {
            if (++attempts < 30) requestAnimationFrame(position);
            return;
        }
        const right = document.documentElement.clientWidth - 8;
        const shift = rect.left < 8 ? 8 - rect.left : rect.right > right ? right - rect.right : 0;
        popover.style.translate = `${Math.round(shift)}px 0`;
    };
    // Let the portal's render and native positioning finish without holding Blazor's after-render callback open.
    requestAnimationFrame(() => requestAnimationFrame(position));
}
