using System.Text.Json;
using Microsoft.JSInterop;

namespace DeveAgentManager.Client.Services;

/// <summary>
/// Light or dark (My settings → Appearance), kept per browser. "system" follows the device, also when it changes (e.g. at
/// night). The page's own colors switch in CSS (terminal.js sets data-theme); this tells MudBlazor.
/// </summary>
public sealed class ThemeService(IJSRuntime js)
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    private DotNetObjectReference<ThemeService>? _self;

    public string Mode { get; private set; } = System;

    public bool IsDark { get; private set; } = true;

    public event Action? Changed;

    /// <summary>Reads the choice (right away, so nothing is drawn in the wrong theme first) and starts following changes.</summary>
    public void Initialize()
    {
        if (_self is not null || js is not IJSInProcessRuntime inProcess)
        {
            return;
        }

        var state = inProcess.Invoke<JsonElement>("damTheme.get");
        Mode = state[0].GetString() ?? System;
        IsDark = state[1].GetBoolean();
        _self = DotNetObjectReference.Create(this);
        inProcess.InvokeVoid("damTheme.watch", _self);
    }

    public async Task SetModeAsync(string mode)
    {
        Mode = mode;
        IsDark = await js.InvokeAsync<bool>("damTheme.set", mode);
        Changed?.Invoke();
    }

    [JSInvokable]
    public void OnThemeChanged(bool dark)
    {
        if (IsDark != dark)
        {
            IsDark = dark;
            Changed?.Invoke();
        }
    }
}
