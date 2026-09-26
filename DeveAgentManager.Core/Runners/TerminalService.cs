using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Runners;

/// <summary>Opens browser terminals: a plain shell on a Runner, or attached to an agent's tmux session.</summary>
public sealed class TerminalService(IDbContextFactory<ManagerDbContext> dbFactory, RunnerShell shell, AgentService agents)
{
    public async Task<TerminalConnection> OpenShellAsync(Guid runnerId, int columns, int rows, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var runner = await db.Runners.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runnerId, cancellationToken)
            ?? throw new ArgumentException("Runner not found.");
        if (runner.State != RunnerState.Ready)
        {
            throw new InvalidOperationException($"Runner '{runner.Name}' is not ready ({runner.State}).");
        }

        return await OpenAsync(runner, null, columns, rows, cancellationToken);
    }

    /// <summary>A terminal that runs one command on a Runner (e.g. a login), instead of a shell.</summary>
    public async Task<TerminalConnection> OpenCommandAsync(Guid runnerId, string command, int columns, int rows, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var runner = await db.Runners.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runnerId, cancellationToken)
            ?? throw new ArgumentException("Runner not found.");
        if (runner.State != RunnerState.Ready)
        {
            throw new InvalidOperationException($"Runner '{runner.Name}' is not ready ({runner.State}).");
        }

        // Through a login shell (the Manager's env.sh), so the harness is on the PATH.
        return await OpenAsync(runner, "bash -lc " + ShellQuote.Quote($". \"{RunnerShell.EnvScript}\" 2>/dev/null; {command}"), columns, rows, cancellationToken);
    }

    public async Task<TerminalConnection> OpenAgentAsync(Guid agentId, int columns, int rows, CancellationToken cancellationToken)
    {
        var (runner, command) = await agents.GetAttachCommandAsync(agentId, cancellationToken);
        return await OpenAsync(runner, command, columns, rows, cancellationToken);
    }

    /// <summary>Largest image that can be pasted into a terminal.</summary>
    public const long MaxImageBytes = 25 * 1024 * 1024;

    /// <summary>The image types harnesses can send to a model (pi: PNG, JPEG, WebP and GIF).</summary>
    private static readonly Dictionary<string, string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = "png",
        ["image/jpeg"] = "jpg",
        ["image/webp"] = "webp",
        ["image/gif"] = "gif",
    };

    /// <summary>Folder on the Runner for images pasted into terminals, outside any repository.</summary>
    public const string UploadFolder = "~/.deveagentmanager/uploads";

    /// <summary>
    /// Stores an image pasted into a terminal on the terminal's Runner and returns its path there. The terminal then gets
    /// the path as text, like pi's own Ctrl+V does with an image from the local clipboard: the harness sends the image
    /// to the model from that file.
    /// </summary>
    public async Task<string> UploadImageAsync(Guid? agentId, Guid? runnerId, string contentType, byte[] bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
        {
            throw new ArgumentException($"Images can be up to {MaxImageBytes / 1024 / 1024} MB.");
        }

        // The type the browser reports can be wrong (Android pickers call screenshots image/jpeg): the content decides.
        var detected = DetectImageType(bytes);
        if (detected is null || !ImageExtensions.TryGetValue(detected, out var extension))
        {
            throw new ArgumentException($"Only PNG, JPEG, WebP and GIF images can be pasted (this is {(string.IsNullOrEmpty(contentType) ? "an unknown type" : contentType)}).");
        }

        var runner = await FindRunnerAsync(agentId, runnerId, cancellationToken);
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.{extension}";
        return await shell.UploadAsync(runner, $"{UploadFolder}/{name}", bytes, "600", cancellationToken);
    }

    /// <summary>The image type from the file's signature, or null when it isn't one of the supported types.</summary>
    internal static string? DetectImageType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return null;
    }

    private async Task<Runner> FindRunnerAsync(Guid? agentId, Guid? runnerId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Runner? runner;
        if (agentId is { } id)
        {
            runner = (await db.Agents.AsNoTracking().Include(a => a.Runner).FirstOrDefaultAsync(a => a.Id == id, cancellationToken))?.Runner;
        }
        else
        {
            runner = await db.Runners.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runnerId, cancellationToken);
        }

        if (runner is null)
        {
            throw new ArgumentException("Runner not found.");
        }

        return runner.State == RunnerState.Ready ? runner : throw new InvalidOperationException($"Runner '{runner.Name}' is not ready ({runner.State}).");
    }

    private async Task<TerminalConnection> OpenAsync(Runner runner, string? command, int columns, int rows, CancellationToken cancellationToken)
    {
        var client = await shell.ConnectNewAsync(runner, cancellationToken);
        try
        {
            return await Task.Run(() => TerminalConnection.Open(client, command, columns, rows), cancellationToken);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
