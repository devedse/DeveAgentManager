using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;

namespace DeveAgentManager.Core.Runners;

/// <summary>Reads the account's actual graphical session without logging in or changing it.</summary>
public sealed class RunnerDesktop(RunnerShell shell)
{
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
