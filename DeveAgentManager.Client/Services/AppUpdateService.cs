using System.Reflection;
using System.Text.Json;
using DeveAgentManager.Contracts;
using Microsoft.JSInterop;

namespace DeveAgentManager.Client.Services;

/// <summary>Checks the deployed build against the assemblies actually running in this browser tab.</summary>
public sealed class AppUpdateService(IJSRuntime js, ManagerApi api, LiveUpdates live) : IAsyncDisposable
{
    private static readonly string CurrentBuild = typeof(AppUpdateService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
    public static string CurrentVersion => CurrentBuild.Split('+')[0];
    private Task? _initialize;
    private Task? _checking;
    private IJSObjectReference? _module;
    private DotNetObjectReference<AppUpdateService>? _self;
    private string? _dismissed;
    private bool _disposed;

    public AppBuildDto? Available { get; private set; }
    public event Action? Changed;

    public Task InitializeAsync() => _initialize ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        _module = await js.InvokeAsync<IJSObjectReference>("import", "./js/app-update.js");
        _self = DotNetObjectReference.Create(this);
        live.Reconnected += OnReconnected;
        await _module.InvokeVoidAsync("start", _self);
        await CheckForUpdate();
    }

    private void OnReconnected() => _ = CheckForUpdate();

    [JSInvokable]
    public Task CheckForUpdate()
    {
        if (_disposed) return Task.CompletedTask;
        return _checking is { IsCompleted: false } ? _checking : _checking = CheckCoreAsync();
    }

    private async Task CheckCoreAsync()
    {
        try
        {
            var build = await api.GetBuildAsync();
            if (_disposed) return;
            var available = build.BuildId != CurrentBuild && build.BuildId != _dismissed ? build : null;
            if (Available == available) return;
            Available = available;
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or JsonException or TaskCanceledException)
        {
            // A deployment or sleeping phone can temporarily make the Manager unavailable; retry on the next check.
        }
    }

    public void Dismiss()
    {
        _dismissed = Available?.BuildId;
        Available = null;
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        live.Reconnected -= OnReconnected;
        if (_module is not null)
            try { await _module.InvokeVoidAsync("stop"); await _module.DisposeAsync(); }
            catch (Exception ex) when (ex is JSDisconnectedException or TaskCanceledException) { }
        _self?.Dispose();
    }
}
