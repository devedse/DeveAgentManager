using Microsoft.JSInterop;

namespace DeveAgentManager.Client.Services;

/// <summary>
/// Whether Shared files and Shared images show only what the conversation's own workspace shared, instead of every
/// workspace of its project. Kept for this tab only (sessionStorage), so a new tab shows all workspaces again; without
/// storage it lasts until the page reloads.
/// </summary>
public sealed class SharedArtifactScope(IJSRuntime js)
{
    private const string Key = "dam-shared-this-workspace";
    private bool? _thisWorkspace;

    public event Action? Changed;

    public async Task<bool> ThisWorkspaceAsync()
    {
        if (_thisWorkspace is null)
        {
            try { _thisWorkspace = await js.InvokeAsync<string?>("sessionStorage.getItem", Key) == "1"; }
            catch (JSException) { _thisWorkspace = false; }
        }
        return _thisWorkspace.Value;
    }

    public async Task SetAsync(bool thisWorkspace)
    {
        if (_thisWorkspace == thisWorkspace) return;
        _thisWorkspace = thisWorkspace;
        try
        {
            if (thisWorkspace) await js.InvokeVoidAsync("sessionStorage.setItem", Key, "1");
            else await js.InvokeVoidAsync("sessionStorage.removeItem", Key);
        }
        catch (JSException) { } // Private browsing may disallow storage.
        Changed?.Invoke();
    }

    /// <summary>The items to show with the setting.</summary>
    public static List<T> Shown<T>(IEnumerable<T> items, Func<T, string?> otherWorkspace, bool thisWorkspace) =>
        thisWorkspace ? items.Where(i => otherWorkspace(i) is null).ToList() : items.ToList();
}
