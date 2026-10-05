using System.Text.Json;
using System.Collections.Concurrent;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Sessions;
using Microsoft.Extensions.Caching.Memory;

namespace DeveAgentManager.Core.Runners;

/// <summary>Reads the account's actual graphical session without logging in or changing it.</summary>
public sealed record DesktopFrame(byte[] Data, DateTime CapturedAt);

public sealed class RunnerDesktop(RunnerShell shell, IMemoryCache cache)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _captureLocks = new();

    /// <summary>One capture per runner at a time; concurrent viewers share a frame. Idle frames expire.</summary>
    public async Task<DesktopFrame> CaptureAsync(Runner runner, CancellationToken ct)
    {
        if (runner.Kind != RunnerKind.Desktop || runner.State != RunnerState.Ready)
            throw new InvalidOperationException("A ready desktop runner is required.");
        var key = (nameof(DesktopFrame), runner.Id);
        var gate = _captureLocks.GetOrAdd(runner.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue<DesktopFrame>(key, out var frame) && frame is not null && DateTime.UtcNow - frame.CapturedAt < TimeSpan.FromSeconds(1)) return frame;
            var path = $"~/.deveagentmanager/desktop-frame-{Guid.NewGuid():N}.jpg";
            try
            {
                var result = await shell.RunAsync(runner, $"umask 077; python3 ~/.deveagentmanager/desktop.py screenshot {path}", TimeSpan.FromSeconds(15), ct);
                result.EnsureSuccess("Capturing desktop");
                var data = await shell.DownloadBytesAsync(runner, path, SessionImageStore.MaxBytes, ct)
                    ?? throw new InvalidOperationException("The desktop did not produce an image.");
                if (SessionImageStore.ContentType(data) != "image/jpeg") throw new InvalidOperationException("The desktop returned an invalid screenshot.");
                frame = new(data, DateTime.UtcNow);
                cache.Set(key, frame, TimeSpan.FromSeconds(2));
                return frame;
            }
            finally
            {
                try { await shell.RunAsync(runner, $"rm -f -- {path}", TimeSpan.FromSeconds(5), CancellationToken.None); }
                catch (Exception) { /* A disconnected runner can leave a temporary frame; setup cleans these. */ }
            }
        }
        finally { gate.Release(); }
    }

    public async Task<DesktopStatusDto> ReadAsync(Runner runner, CancellationToken ct = default)
    {
        if (runner.Kind != RunnerKind.Desktop) return new("unavailable", "This is a headless runner.");
        if (runner.State != RunnerState.Ready) return new("unavailable", "The runner is not ready.");
        const string script = """
            if [ -x "$HOME/.deveagentmanager/desktop.py" ]; then
              python3 "$HOME/.deveagentmanager/desktop.py" status
            else
              printf '%s\n' '{"state":"setup-required","message":"Re-run setup to install desktop autologin and UI tools."}'
            fi
            """;
        try
        {
            var result = await shell.RunAsync(runner, script, TimeSpan.FromSeconds(15), ct);
            return result.Succeeded ? Parse(result.Output) : new("unavailable", "Desktop observation failed. Check the setup log and runner connection.");
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new("unavailable", "Desktop observation failed. Check the setup log and runner connection.");
        }
    }

    internal static DesktopStatusDto Parse(string json)
    {
        try
        {
            if (json.Length <= 4096 && JsonSerializer.Deserialize<DesktopStatusDto>(json, ApiJson.Options) is { } dto
                && dto.State is "ready" or "login-required" or "unavailable" or "setup-required"
                && !string.IsNullOrWhiteSpace(dto.Message)) return dto;
        }
        catch (JsonException) { }
        return new("unavailable", "The runner returned an invalid desktop status.");
    }
}
