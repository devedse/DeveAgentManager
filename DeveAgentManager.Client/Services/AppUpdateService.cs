using System.Reflection;
using System.Text.Json;
using DeveAgentManager.Contracts;

namespace DeveAgentManager.Client.Services;

/// <summary>Checks the deployed build against the assemblies actually running in this browser tab.</summary>
public sealed class AppUpdateService(ManagerApi api, LiveUpdates live, PageVisibility visibility) : IAsyncDisposable
{
    private static readonly string CurrentBuild = typeof(AppUpdateService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
    public static string CurrentVersion => CurrentBuild.Split('+')[0];
    private Task? _initialize;
    private Task? _checking;
    private readonly CancellationTokenSource _stop = new();
    private Task? _poll;
    private string? _dismissed;
    private bool _disposed;

    public AppBuildDto? Available { get; private set; }
    public event Action? Changed;

    public Task InitializeAsync() => _initialize ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        await visibility.InitializeAsync();
        visibility.VisibleObserved += OnReconnected;
        _poll = PollAsync();
        live.Reconnected += OnReconnected;
        await CheckForUpdate();
    }

    private void OnReconnected() => _ = CheckForUpdate();

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
        visibility.VisibleObserved -= OnReconnected;
        _stop.Cancel();
        if (_poll is not null) await _poll;
        _stop.Dispose();
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
                if (visibility.Visible) await CheckForUpdate();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
}
