using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Ssh;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Runners;

/// <summary>Slow update checks run separately from terminal and conversation polling. Checks never install software.</summary>
public sealed class RunnerUpdateMonitor(RunnerService runners, RunnerShell shell, RunnerUpdateCache cache,
    RunnerReleaseCatalog releases, IEnumerable<IHarness> harnesses, IEnumerable<IRunnerTool> tools,
    ManagerEvents events, TimeProvider clock, ILogger<RunnerUpdateMonitor> logger) : BackgroundService
{
    private sealed record Software(string Id, string Name, string Command, string? Source, bool GitHub = false);
    private readonly Software[] _software = harnesses.Select(h => new Software(h.Id, h.DisplayName, h.VersionCommand, h.UpdateNpmPackage))
        .Concat(tools.Select(t => new Software(t.Id, t.DisplayName, t.VersionCommand, t.UpdateGitHubRepository, true))).ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                var all = await runners.ListAsync(stoppingToken);
                cache.RemoveMissing(all.Select(r => r.Id).ToHashSet());
                await Parallel.ForEachAsync(all.Where(r => r.State == RunnerState.Ready && runners.CurrentOperation(r.Id) is null),
                    new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = stoppingToken }, CheckAsync);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Runner update check pass failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async ValueTask CheckAsync(Runner runner, CancellationToken ct)
    {
        var check = cache.Begin(runner.Id, clock.GetUtcNow().UtcDateTime);
        if (check is null) return;
        events.RunnerChanged(runner.Id);
        var value = cache.Get(runner.Id)!;
        try
        {
            // Operations publish their busy state before awaiting us. This closes the race with a queued check.
            if (runners.CurrentOperation(runner.Id) is not null) { cache.Request(runner.Id); return; }
            if (check.Software)
            {
                try
                {
                    var software = _software.Where(s => runner.HarnessIds.Contains(s.Id) || runner.ToolIds.Contains(s.Id)).ToArray();
                    var script = "set -o pipefail\n" + string.Join('\n', software.Select(s =>
                        $"version=$({s.Command} 2>/dev/null) || version=''; printf '%s=%s\\n' {ShellQuote.Quote(s.Id)} \"$version\""));
                    var output = (await shell.RunAsync(runner, script, TimeSpan.FromMinutes(2), ct)).EnsureSuccess("Reading installed versions").Output;
                    var installed = output.Split('\n').Select(l => l.Split('=', 2)).Where(p => p.Length == 2)
                        .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => RunnerReleaseCatalog.NormalizeVersion(g.First()[1]));
                    var observations = new List<RunnerSoftwareUpdateDto>();
                    foreach (var item in software)
                    {
                        installed.TryGetValue(item.Id, out var version);
                        var latest = item.Source is null ? null : await releases.LatestAsync(item.Source, item.GitHub, ct, check.RefreshCatalog);
                        var error = item.Source is null ? "Update checks unavailable" : version is null ? "Installed version unavailable" : latest?.Error;
                        observations.Add(new(item.Id, item.Name, version, latest?.Version,
                            RunnerReleaseCatalog.IsNewer(version, latest?.Version), error));
                    }
                    value = value with { Software = observations, SoftwareCheckedAt = clock.GetUtcNow().UtcDateTime,
                        SoftwareError = observations.Any(s => s.Error is not null) ? "Some tool updates could not be checked" : null };
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogDebug(ex, "Could not check installed software on runner {RunnerId}", runner.Id);
                    value = value with { SoftwareError = "Could not read installed versions" };
                }
            }
            // Show tool results while the slower package-list refresh is still running.
            cache.Publish(runner.Id, check, value);
            events.RunnerChanged(runner.Id);
            // Don't start apt if maintenance began while the version commands ran.
            if (runners.CurrentOperation(runner.Id) is not null) { cache.Request(runner.Id); return; }
            if (check.System)
            {
                try
                {
                    var script = RunnerSetupService.ReadResource("check-system-updates.sh");
                    var result = (await shell.RunAsync(runner, script, TimeSpan.FromMinutes(4), ct)).EnsureSuccess("Checking system packages");
                    var (count, reboot) = ParseSystem(result.Output);
                    var packages = ParseSystemPackages(result.Output, count);
                    value = value with { SystemPackageCount = count, SystemPackages = packages, RebootRequired = reboot,
                        SystemCheckedAt = clock.GetUtcNow().UtcDateTime, SystemError = null };
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogDebug(ex, "Could not check system updates on runner {RunnerId}", runner.Id);
                    value = value with { SystemError = "Could not refresh or check system packages" };
                }
            }
        }
        finally
        {
            cache.Complete(runner.Id, check, value, clock.GetUtcNow().UtcDateTime);
            events.RunnerChanged(runner.Id);
        }
    }

    internal static (int Count, bool Reboot) ParseSystem(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var counts = lines.Where(l => l.StartsWith("DAM_PACKAGES=", StringComparison.Ordinal)).ToArray();
        var reboots = lines.Where(l => l.StartsWith("DAM_REBOOT=", StringComparison.Ordinal)).ToArray();
        if (counts.Length != 1 || !int.TryParse(counts[0][13..], out var count) || count < 0 || reboots.Length != 1 || reboots[0] is not ("DAM_REBOOT=0" or "DAM_REBOOT=1"))
            throw new FormatException("Incomplete system update observation.");
        return (count, reboots[0] == "DAM_REBOOT=1");
    }

    internal static IReadOnlyList<RunnerSystemPackageUpdateDto>? ParseSystemPackages(string output, int count)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var markers = lines.Where(line => line.StartsWith("DAM_PACKAGE_DETAILS=", StringComparison.Ordinal)).ToArray();
        if (markers.Length == 0) return null; // Older count-only observations have no package names.
        if (markers.Length != 1 || markers[0].TrimEnd('\r') != "DAM_PACKAGE_DETAILS=1")
            throw new FormatException("Invalid system package details marker.");
        var packages = new List<RunnerSystemPackageUpdateDto>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines.Where(line => line.StartsWith("DAM_PACKAGE\t", StringComparison.Ordinal)))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length != 4 || !Valid(fields[1]) || !Valid(fields[3])
                || fields[2].Length > 0 && !Valid(fields[2]) || !names.Add(fields[1])
                || packages.Count >= RunnerUpdatesDto.MaxSystemPackageDetails)
                throw new FormatException("Invalid system package update details.");
            packages.Add(new(fields[1], fields[2].Length == 0 ? null : fields[2], fields[3]));
        }
        if (packages.Count != Math.Min(count, RunnerUpdatesDto.MaxSystemPackageDetails))
            throw new FormatException("Incomplete system package update details.");
        return packages;

        static bool Valid(string value) => value.Length is > 0 and <= 200 && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));
    }
}
