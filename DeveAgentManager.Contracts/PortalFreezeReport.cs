namespace DeveAgentManager.Contracts;

/// <summary>
/// The Portal stopped answering in a browser (freeze-watch.ts): sent by a worker, which keeps running while the page
/// doesn't, once the page hasn't answered for a while, and again if it answers again. The Manager writes it to its log.
/// </summary>
/// <param name="Recovered">False while the page is still frozen; true once it answered again.</param>
/// <param name="Seconds">How long the page didn't answer: so far, or in all once it recovered.</param>
/// <param name="Page">The page's path when it stopped answering.</param>
/// <param name="OpenForSeconds">How long the page had been open (since it was loaded or reloaded).</param>
/// <param name="JsHeapBytes">JavaScript memory in use (Chromium only).</param>
/// <param name="WasmBytes">The .NET runtime's memory (WebAssembly), which only grows.</param>
/// <param name="Elements">Elements on the page.</param>
/// <param name="Notes">What happened before, oldest first.</param>
public sealed record PortalFreezeReport(
    bool Recovered,
    double Seconds,
    string? Page,
    double? OpenForSeconds,
    long? JsHeapBytes,
    long? WasmBytes,
    int? Elements,
    string? Browser,
    IReadOnlyList<PortalFreezeNote>? Notes);

/// <summary>Something the page did: a click, a navigation, a slow frame, a section loading.</summary>
/// <param name="Seconds">When, relative to the last time the page answered (negative: before).</param>
public sealed record PortalFreezeNote(double Seconds, string? Kind, string? Detail);
