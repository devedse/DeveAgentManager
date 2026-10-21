using Microsoft.JSInterop;

namespace DeveAgentManager.Client.Services;

/// <summary>
/// Whether this browser tab is shown. Live sections pause their reloads while it is hidden and catch up once when it
/// is shown again, so tabs left open in the background don't keep the Manager busy.
/// </summary>
public sealed class PageVisibility(IJSRuntime js) : IAsyncDisposable
{
    private Task? _initialize;
    private IJSObjectReference? _module;
    private DotNetObjectReference<PageVisibility>? _self;

    /// <summary>True until the browser reports otherwise.</summary>
    public bool Visible { get; private set; } = true;

    /// <summary>The tab was hidden and is shown again.</summary>
    public event Action? Shown;

    public Task InitializeAsync() => _initialize ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        try
        {
            _module = await js.InvokeAsync<IJSObjectReference>("import", "./js/page-visibility.js");
            _self = DotNetObjectReference.Create(this);
            Report(await _module.InvokeAsync<bool>("start", _self));
        }
        catch (JSException)
        {
            // Without it, sections keep reloading as if the tab were shown.
        }
    }

    [JSInvokable]
    public void Report(bool visible)
    {
        var shown = visible && !Visible;
        Visible = visible;
        if (shown) Shown?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module is not null) await _module.InvokeVoidAsync("stop");
        }
        catch (JSDisconnectedException) { }
        _self?.Dispose();
    }
}
