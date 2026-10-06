using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Setup;
using DeveAgentManager.Core.Ssh;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using Renci.SshNet.Common;

namespace DeveAgentManager.Core.Runners;

/// <summary>
/// Sets up a Runner over SSH: base packages, tmux and git config, the shared Setup Profile, the selected harnesses and
/// tools, and what each person with access has on it (<see cref="RunnerUsers"/>).
/// </summary>
public sealed partial class RunnerSetupService(
    RunnerShell shell,
    ManagerSshKey sshKey,
    SettingsStore settings,
    RunnerUsers users,
    RunnerPasswords passwords,
    IEnumerable<IHarness> harnesses,
    IEnumerable<IRunnerTool> tools)
{
    /// <summary>Bump when runner-setup.sh changes, so the Portal can show that a Runner's setup is outdated.</summary>
    public const int SetupVersion = 5;

    /// <summary>The first setup version that installs dam-tools.</summary>
    public const int AgentToolsSetupVersion = 4;

    private static readonly Lazy<string> SetupScript = new(() => ReadResource("runner-setup.sh"));

    private readonly Dictionary<string, IHarness> _harnesses = harnesses.ToDictionary(h => h.Id);
    private readonly Dictionary<string, IRunnerTool> _tools = tools.ToDictionary(t => t.Id);

    public IReadOnlyCollection<IRunnerTool> Tools => _tools.Values;

    /// <summary>Waits until SSH answers (a fresh VM needs a moment after it got its IP).</summary>
    public async Task WaitForSshAsync(Runner runner, IRunnerLog log, TimeSpan timeout, CancellationToken cancellationToken)
    {
        log.Step($"Connecting over SSH to {runner.Username}@{runner.Host}:{runner.SshPort}");
        var attempt = 0;
        var pipeline = new ResiliencePipelineBuilder()
            .AddTimeout(timeout)
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(e => e is not HostKeyMismatchException and not OperationCanceledException),
                MaxRetryAttempts = int.MaxValue,
                Delay = TimeSpan.FromSeconds(5),
                BackoffType = DelayBackoffType.Constant,
                OnRetry = args =>
                {
                    if (++attempt % 3 == 1)
                    {
                        log.Info($"SSH not ready yet ({args.Outcome.Exception?.Message}), retrying...");
                    }

                    shell.Invalidate(runner.Id);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();

        try
        {
            await pipeline.ExecuteAsync(async ct => (await shell.RunAsync(runner, "true", TimeSpan.FromSeconds(30), ct)).EnsureSuccess("SSH test"), cancellationToken);
        }
        catch (TimeoutRejectedException)
        {
            throw new TimeoutException($"SSH on {runner.Host}:{runner.SshPort} did not become available within {timeout.TotalMinutes:0} minutes.");
        }

        log.Success($"Connected (host key {runner.HostKeyFingerprint})");
    }

    public async Task RunAsync(Runner runner, IRunnerLog log, CancellationToken cancellationToken)
    {
        var profile = await settings.GetSetupProfileAsync(cancellationToken);

        log.Step("Authorizing the Manager's SSH key");
        var quotedKey = ShellQuote.Quote(sshKey.PublicKey);
        (await shell.RunAsync(runner,
            $"mkdir -p ~/.ssh && chmod 700 ~/.ssh && touch ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys && (grep -qxF {quotedKey} ~/.ssh/authorized_keys || echo {quotedKey} >> ~/.ssh/authorized_keys)",
            cancellationToken: cancellationToken)).EnsureSuccess("Authorizing SSH key");

        runner.HomeDirectory = (await shell.RunAsync(runner, "printf %s \"$HOME\"", cancellationToken: cancellationToken)).EnsureSuccess("Reading home directory").Output.Trim();

        log.Step("Uploading setup files");
        await shell.UploadAsync(runner, "~/.deveagentmanager/agent-env.sh", BuildAgentEnv(runner.Kind == RunnerKind.Desktop), "600", cancellationToken);
        await shell.UploadAsync(runner, "~/.deveagentmanager/runner-setup.sh", SetupScript.Value, "700", cancellationToken);

        await RunScriptAsync(runner, log, "Base setup", "bash ~/.deveagentmanager/runner-setup.sh", TimeSpan.FromMinutes(30), cancellationToken);
        await passwords.ApplyAtSetupAsync(runner, log, cancellationToken);

        // dam-tools: commands for agents, e.g. dam-tools share-image. dam-share was its earlier name.
        await shell.UploadAsync(runner, "~/.deveagentmanager/dam-tools.py", ReadResource("dam-tools.py"), "700", cancellationToken);
        await RunScriptAsync(runner, log, "Agent tools", "mkdir -p ~/.local/bin; ln -sf ~/.deveagentmanager/dam-tools.py ~/.local/bin/dam-tools; rm -f ~/.local/bin/dam-share ~/.deveagentmanager/share-image.py", TimeSpan.FromSeconds(30), cancellationToken);

        if (runner.Kind == RunnerKind.Desktop)
        {
            await shell.UploadAsync(runner, "~/.deveagentmanager/desktop.py", ReadResource("desktop.py"), "700", cancellationToken);
            await shell.UploadAsync(runner, "~/.deveagentmanager/desktop-setup.sh", ReadResource("desktop-setup.sh"), "700", cancellationToken);
            await shell.UploadAsync(runner, "~/.deveagentmanager/desktop-shell.sh", ReadResource("desktop-shell.sh"), "600", cancellationToken);
            await RunScriptAsync(runner, log, "Desktop autologin and UI tools", "bash ~/.deveagentmanager/desktop-setup.sh", TimeSpan.FromMinutes(15), cancellationToken);
        }

        foreach (var file in profile.Files)
        {
            log.Info($"Writing {file.Path}");
            await shell.UploadAsync(runner, file.Path, file.Content.ReplaceLineEndings("\n"), NormalizeMode(file.Mode), cancellationToken);
        }

        foreach (var harnessId in runner.HarnessIds)
        {
            if (!_harnesses.TryGetValue(harnessId, out var harness))
            {
                log.Warning($"Unknown harness '{harnessId}', skipped.");
                continue;
            }

            await RunScriptAsync(runner, log, $"Installing {harness.DisplayName}", harness.InstallScript, TimeSpan.FromMinutes(15), cancellationToken);
            if (harness.ConfigSupport is not null)
            {
                // Also with an empty configuration: that removes packages installed by an earlier setup.
                await RunScriptAsync(runner, log, $"Configuring {harness.DisplayName} (packages, model, settings)",
                    harness.BuildConfigureScript(profile.GetHarness(harness.Id)), TimeSpan.FromMinutes(15), cancellationToken);
            }
        }

        foreach (var toolId in runner.ToolIds)
        {
            if (_tools.TryGetValue(toolId, out var tool))
            {
                await RunScriptAsync(runner, log, $"Installing {tool.DisplayName}", tool.InstallScript, TimeSpan.FromMinutes(15), cancellationToken);
            }
            else
            {
                log.Warning($"Unknown tool '{toolId}', skipped.");
            }
        }

        if (!string.IsNullOrWhiteSpace(profile.Script))
        {
            await shell.UploadAsync(runner, "~/.deveagentmanager/profile-script.sh", profile.Script.ReplaceLineEndings("\n"), "700", cancellationToken);
            await RunScriptAsync(runner, log, "Running the Setup Profile script", "bash -e ~/.deveagentmanager/profile-script.sh", TimeSpan.FromMinutes(30), cancellationToken);
        }

        if (runner.Kind == RunnerKind.Desktop)
        {
            await RunScriptAsync(runner, log, "Removing old desktop instructions", "python3 ~/.deveagentmanager/desktop.py remove-instructions", TimeSpan.FromSeconds(30), cancellationToken);
        }

        log.Step("Setting up the people with access: their git identity, GitHub token, environment and SSH keys");
        await users.SyncAsync(runner, cancellationToken);

        runner.InstalledVersions = await ReadVersionsAsync(runner, cancellationToken);
        runner.SetupVersion = SetupVersion;
        runner.LastSetupAt = DateTime.UtcNow;
        log.Success($"Setup finished: {runner.InstalledVersions}");
    }

    /// <summary>
    /// The models a harness offers, from its own catalog on one of these Runners (the first that answers). Empty when the
    /// harness has no catalog or no Runner has it.
    /// </summary>
    public async Task<IReadOnlyList<ModelChoice>> ListModelsAsync(string harnessId, IEnumerable<Runner> runners, CancellationToken cancellationToken = default)
    {
        var harness = GetHarness(harnessId);
        if (harness.ModelsCommand is not { } command)
        {
            return [];
        }

        foreach (var runner in runners.Where(r => r.State == RunnerState.Ready && r.HarnessIds.Contains(harnessId)))
        {
            try
            {
                var result = await shell.RunAsync(runner, command, TimeSpan.FromSeconds(60), cancellationToken);
                if (result.Succeeded && harness.ParseModels(result.Output) is { Count: > 0 } models)
                {
                    return models;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Try the next Runner.
                _ = ex;
            }
        }

        return [];
    }

    public IHarness GetHarness(string harnessId) =>
        _harnesses.GetValueOrDefault(harnessId) ?? throw new ArgumentException($"Unknown harness '{harnessId}'.");

    public IRunnerTool GetTool(string toolId) =>
        _tools.GetValueOrDefault(toolId) ?? throw new ArgumentException($"Unknown tool '{toolId}'.");

    /// <summary>Installs the latest version of a tool (its install script also updates) and refreshes the versions shown.</summary>
    public async Task UpdateToolAsync(Runner runner, string toolId, IRunnerLog log, CancellationToken cancellationToken = default)
    {
        var tool = GetTool(toolId);
        var before = runner.InstalledVersions;
        await RunScriptAsync(runner, log, $"Installing the latest {tool.DisplayName}", tool.InstallScript, TimeSpan.FromMinutes(15), cancellationToken);
        runner.InstalledVersions = await ReadVersionsAsync(runner, cancellationToken);
        log.Success(before == runner.InstalledVersions ? $"{tool.DisplayName} is up to date: {runner.InstalledVersions}" : $"{tool.DisplayName} installed: {runner.InstalledVersions}");
    }

    /// <summary>
    /// Installs the latest version of a harness and its packages (its install script is idempotent and updates), then
    /// applies its configuration and refreshes the versions shown for the Runner.
    /// </summary>
    public async Task UpdateHarnessAsync(Runner runner, string harnessId, IRunnerLog log, CancellationToken cancellationToken = default)
    {
        var harness = GetHarness(harnessId);
        if (!runner.HarnessIds.Contains(harnessId))
        {
            throw new InvalidOperationException($"{harness.DisplayName} is not installed on '{runner.Name}'.");
        }

        var profile = await settings.GetSetupProfileAsync(cancellationToken);
        var before = runner.InstalledVersions;
        await RunScriptAsync(runner, log, $"Installing the latest {harness.DisplayName} and updating its packages", harness.InstallScript, TimeSpan.FromMinutes(15), cancellationToken);
        if (harness.ConfigSupport is not null)
        {
            await RunScriptAsync(runner, log, $"Configuring {harness.DisplayName} (packages, model, settings)",
                harness.BuildConfigureScript(profile.GetHarness(harness.Id)), TimeSpan.FromMinutes(15), cancellationToken);
        }

        runner.InstalledVersions = await ReadVersionsAsync(runner, cancellationToken);
        log.Success(before == runner.InstalledVersions
            ? $"{harness.DisplayName} is up to date: {runner.InstalledVersions}"
            : $"{harness.DisplayName} updated: {runner.InstalledVersions}. Restart running agents to use it.");
    }

    /// <summary>
    /// Applies a harness's configuration from the Setup Profile to a Runner without the rest of the setup, e.g. after the
    /// package list changed. Running agents pick up the packages after a restart (pi: or <c>/reload</c>).
    /// </summary>
    public async Task ApplyHarnessConfigAsync(Runner runner, string harnessId, IRunnerLog log, CancellationToken cancellationToken = default)
    {
        var harness = GetHarness(harnessId);
        if (harness.ConfigSupport is null)
        {
            throw new InvalidOperationException($"{harness.DisplayName} has no packages or settings to apply.");
        }

        var profile = await settings.GetSetupProfileAsync(cancellationToken);
        await RunScriptAsync(runner, log, $"Configuring {harness.DisplayName} (packages, model, settings)",
            harness.BuildConfigureScript(profile.GetHarness(harness.Id)), TimeSpan.FromMinutes(15), cancellationToken);
    }

    public async Task<string> ReadVersionsAsync(Runner runner, CancellationToken cancellationToken)
    {
        var commands = new List<(string Name, string Command)>();
        foreach (var harnessId in runner.HarnessIds)
        {
            if (_harnesses.TryGetValue(harnessId, out var harness))
            {
                commands.Add((harness.DisplayName, harness.VersionCommand));
            }
        }

        foreach (var toolId in runner.ToolIds)
        {
            if (_tools.TryGetValue(toolId, out var tool))
            {
                commands.Add((tool.DisplayName, tool.VersionCommand));
            }
        }

        commands.Add(("node", "node --version"));
        commands.Add(("tmux", "tmux -V | cut -d' ' -f2"));

        var script = string.Join("\n", commands.Select(c => $"echo \"{c.Name}=$({c.Command} 2>/dev/null | head -n1)\""));
        var result = await shell.RunAsync(runner, script, cancellationToken: cancellationToken);
        return string.Join(" · ", result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2 && p[1].Trim().Length > 0)
            .Select(p => $"{p[0]} {p[1].Trim()}"));
    }

    /// <summary>
    /// Copies a harness's login and settings files from a Runner into the Setup Profile, so every Runner set up afterwards
    /// gets them. Returns the files that were found.
    /// </summary>
    public async Task<IReadOnlyList<string>> ImportHarnessFilesAsync(Runner runner, string harnessId, CancellationToken cancellationToken = default)
    {
        var harness = _harnesses.GetValueOrDefault(harnessId) ?? throw new ArgumentException($"Unknown harness '{harnessId}'.");
        var profile = await settings.GetSetupProfileAsync(cancellationToken);
        var imported = new List<string>();
        foreach (var path in harness.ConfigFiles)
        {
            var content = await shell.DownloadAsync(runner, path, cancellationToken);
            if (content is null)
            {
                continue;
            }

            profile.Files.RemoveAll(f => f.Path == path);
            profile.Files.Add(new SetupFile { Path = path, Content = content, Mode = "600" });
            imported.Add(path);
        }

        if (imported.Count > 0)
        {
            await settings.SaveSetupProfileAsync(profile, cancellationToken);
        }

        return imported;
    }

    private async Task RunScriptAsync(Runner runner, IRunnerLog log, string title, string script, TimeSpan timeout, CancellationToken cancellationToken)
    {
        log.Step(title);
        var exitCode = await shell.RunStreamingAsync(runner, script, line =>
        {
            if (line.StartsWith("==> ", StringComparison.Ordinal))
            {
                log.Step(line[4..]);
            }
            else if (line.Length > 0)
            {
                log.Output(line);
            }
        }, timeout, cancellationToken);

        if (exitCode != 0)
        {
            throw new RunnerCommandException($"{title} failed with exit code {exitCode}. See the setup log for details.");
        }
    }

    /// <summary>
    /// What every shell and agent on the Runner loads (through env.sh), whoever it is for: on a desktop Runner, the desktop
    /// connection. Each user's own environment comes after it (<see cref="RunnerUsers.EnvironmentFile"/>).
    /// </summary>
    internal static string BuildAgentEnv(bool desktop = false)
    {
        var builder = new StringBuilder("# Managed by DeveAgentManager: the environment of every shell and agent on this Runner\n");
        if (desktop)
        {
            builder.Append("""
                if [ "${BASH_ENV:-}" != "$HOME/.deveagentmanager/desktop-shell.sh" ]; then
                  export DAM_PREVIOUS_BASH_ENV="${BASH_ENV:-}"
                fi
                export BASH_ENV="$HOME/.deveagentmanager/desktop-shell.sh"
                [ ! -f "$HOME/.deveagentmanager/desktop-env.sh" ] || . "$HOME/.deveagentmanager/desktop-env.sh"

                """);
        }
        return builder.ToString();
    }

    private static string NormalizeMode(string? mode) =>
        mode is not null && Regex.IsMatch(mode.Trim(), "^[0-7]{3,4}$") ? mode.Trim() : "600";

    internal static string ReadResource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        // Scripts must have Unix line endings, even when the repo was checked out on Windows.
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

}
