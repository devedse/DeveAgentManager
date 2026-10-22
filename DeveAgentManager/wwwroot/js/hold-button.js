// Keeps a hold-to-confirm bubble (HoldButton) in view: shifted sideways where it would run off the page's content area
// (under the navigation drawer or off the screen), and below the button when there is no room above it.
export function place(root) {
    const bubble = root?.querySelector('.hold-bubble');
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
