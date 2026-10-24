// A toolbar that keeps what fits: while it overflows, or the name gets too narrow to read, it hides its items in
// priority order (the first listed goes first) and tells Blazor which ones, so the ⋮ menu offers those instead.
// It only sets the `hidden` attribute, which Blazor never renders on these elements, so a re-render keeps it.
interface ToolbarState {
    toolbar: HTMLElement;
    dotnet: DotNetReference;
    items: { key: string; selector: string }[];
    nameSelector: string;
    reported: string | null;
    resize: ResizeObserver | null;
    mutation: MutationObserver | null;
}
const toolbars = new WeakMap<HTMLElement, ToolbarState>();

// The name keeps at least this many pixels (or its full width when it is shorter).
const minName = 72;

export function start(toolbar: HTMLElement, dotnet: DotNetReference, items: ToolbarState["items"], nameSelector: string) {
    stop(toolbar);
    const state: ToolbarState = { resize: null, mutation: null, toolbar, dotnet, items, nameSelector, reported: null };
    const update = () => layout(state);
    state.resize = new ResizeObserver(update);
    state.resize.observe(toolbar);
    // Content changes too: an agent loads, a file arrives, the state text changes.
    state.mutation = new MutationObserver(update);
    state.mutation.observe(toolbar, { childList: true, subtree: true, characterData: true });
    document.fonts?.ready.then(update);
    toolbars.set(toolbar, state);
    update();
}

export function stop(toolbar: HTMLElement) {
    const state = toolbars.get(toolbar);
    if (!state) return;
    state.resize.disconnect();
    state.mutation.disconnect();
    toolbars.delete(toolbar);
}

function layout(state: ToolbarState) {
    const { toolbar } = state;
    if (!toolbar.isConnected) return;
    const found = state.items.map(item => ({ key: item.key, element: toolbar.querySelector<HTMLElement>(item.selector) })).filter(item => item.element);
    // Start from everything shown; all of this happens before the browser paints.
    for (const item of found) item.element.hidden = false;
    const name = toolbar.querySelector(state.nameSelector);
    // A dropdown can clip its own contents, making scrollWidth look as though its title fits.
    // Also check the name row so the picker and status cannot spill into the action buttons.
    const nameRow = name?.parentElement;
    const fits = () => toolbar.scrollWidth <= toolbar.clientWidth + 1
        && (!nameRow || nameRow.scrollWidth <= nameRow.clientWidth + 1)
        && !(name && name.scrollWidth > name.clientWidth + 1 && name.clientWidth < minName);
    const hidden = [];
    for (const item of found) {
        if (fits()) break;
        // Already hidden by the stylesheet (e.g. quick commands on touch screens, which have the key bar): not in the menu either.
        if (item.element.getClientRects().length === 0) continue;
        item.element.hidden = true;
        hidden.push(item.key);
    }
    const reported = hidden.join(' ');
    if (reported !== state.reported) {
        state.reported = reported;
        state.dotnet.invokeMethodAsync('ToolbarOverflowChanged', hidden).catch(() => { });
    }
}
