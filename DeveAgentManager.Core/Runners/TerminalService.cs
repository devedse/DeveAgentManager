using System.Text.RegularExpressions;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Runners;

/// <summary>Opens browser terminals: a shell on a Runner, or attached to an agent's tmux session.</summary>
public sealed class TerminalService(IDbContextFactory<ManagerDbContext> dbFactory, RunnerShell shell, AgentService agents, RunnerUsers users)
{
    /// <summary>A shell with <paramref name="userId"/>'s environment: their git identity, GitHub token and variables.</summary>
    public async Task<TerminalConnection> OpenShellAsync(Guid runnerId, Guid userId, int columns, int rows, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var runner = await db.Runners.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runnerId, cancellationToken)
            ?? throw new ArgumentException("Runner not found.");
        if (runner.State != RunnerState.Ready)
        {
            throw new InvalidOperationException($"Runner '{runner.Name}' is not ready ({runner.State}).");
        }

        await users.EnsureAsync(runner, userId, cancellationToken);
        // A login shell after the user's environment. The terminal hid its start-up with stty -echo: a shell needs it back.
        var start = $"stty echo 2>/dev/null; . \"{RunnerUsers.EnvironmentFile(userId)}\" 2>/dev/null; exec bash -il";
        return await OpenAsync(runner, "bash -c " + ShellQuote.Quote(start), columns, rows, cancellationToken);
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

    /// <summary>Attached to an agent's tmux session; <paramref name="readOnly"/> for people who can only watch it.</summary>
    public async Task<TerminalConnection> OpenAgentAsync(Guid agentId, bool readOnly, int columns, int rows, CancellationToken cancellationToken)
    {
        var (runner, command) = await agents.GetAttachCommandAsync(agentId, readOnly, cancellationToken);
        return await OpenAsync(runner, command, columns, rows, cancellationToken);
    }

    /// <summary>Largest file (an image, a log, …) that can be pasted into a terminal.</summary>
    public const long MaxUploadBytes = 25 * 1024 * 1024;

    /// <summary>The image types harnesses can send to a model (pi: PNG, JPEG, WebP and GIF).</summary>
    private static readonly Dictionary<string, string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = "png",
        ["image/jpeg"] = "jpg",
        ["image/webp"] = "webp",
        ["image/gif"] = "gif",
    };

    /// <summary>Folder on the Runner for files pasted into terminals, outside any repository.</summary>
    public const string UploadFolder = "~/.deveagentmanager/uploads";

    /// <summary>
    /// Stores a file pasted into a terminal (a screenshot, a log, …) on the terminal's Runner and returns its path there.
    /// The terminal then gets the path as text, like pi's own Ctrl+V does with an image from the local clipboard: the
    /// harness sends an image to the model from that file, and the agent can read any other file.
    /// </summary>
    public async Task<string> UploadAsync(Guid? agentId, Guid? runnerId, string? fileName, byte[] bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length == 0 || bytes.Length > MaxUploadBytes)
        {
            throw new ArgumentException($"Files can be up to {MaxUploadBytes / 1024 / 1024} MB, and not empty.");
        }

        var runner = await FindRunnerAsync(agentId, runnerId, cancellationToken);
        return await shell.UploadAsync(runner, $"{UploadFolder}/{UploadName(fileName, bytes, DateTime.Now)}", bytes, "600", cancellationToken);
    }

    /// <summary>
    /// Where a pasted file goes in <see cref="UploadFolder"/>. An image (by its content: the type the browser reports can
    /// be wrong, Android pickers call screenshots image/jpeg) gets a fresh name with the extension harnesses expect. Any
    /// other file keeps its own name, in a folder of its own, so the agent sees what it is.
    /// </summary>
    internal static string UploadName(string? fileName, ReadOnlySpan<byte> bytes, DateTime now)
    {
        var unique = $"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        return DetectImageType(bytes) is { } type && ImageExtensions.TryGetValue(type, out var extension)
            ? $"{unique}.{extension}"
            : $"{unique}/{SafeFileName(fileName)}";
    }

    /// <summary>A file's name without folders, spaces or shell characters: it is pasted into the terminal as one path.</summary>
    internal static string SafeFileName(string? fileName)
    {
        var name = Regex.Replace(Path.GetFileName((fileName ?? "").Replace('\\', '/')), "[^A-Za-z0-9._-]+", "-").Trim('-', '.');
        if (name.Length > 100)
        {
            var extension = Path.GetExtension(name);
            name = name[..(100 - extension.Length)] + extension;
        }

        return name.Length == 0 ? "file" : name;
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
