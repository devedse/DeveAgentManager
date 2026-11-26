using Microsoft.JSInterop;

namespace DeveAgentManager.Client.Services;

/// <summary>
/// Tells the page's freeze watch (freeze-watch.ts) which sections load and when they finish, so a report of a frozen
/// Portal in the Manager's log says what was running when it stopped. See docs/portal-freezes.md.
/// </summary>
public static class FreezeWatch
{
    private static IJSInProcessRuntime? _js;

    public static void Start(IJSRuntime js) => _js = js as IJSInProcessRuntime;

    public static void Note(string kind, string detail)
    {
        if (_js is not { } js) return;
        try { js.InvokeVoid("damFreezeWatch.note", kind, detail); }
        catch (JSException) { _js = null; } // This browser has no watch (no workers): nothing to tell.
    }
}
