using System.Net;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Ssh;
using Xunit;

namespace DeveAgentManager.Tests;

public class RunnerUpdateTests
{
    [Theory]
    [InlineData("0.9.0", "0.10.0", true)]
    [InlineData("v2.1.0", "2.1.0", false)]
    [InlineData("codex-cli 0.163.0", "0.164.0", true)]
    [InlineData("2.0.0", "1.9.0", false)]
    [InlineData("1.0.0-beta.9", "1.0.0-beta.10", true)]
    [InlineData("1.0.0-beta.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0-beta.1", false)]
    [InlineData("1.0.0+build1", "1.0.0+build2", false)]
    [InlineData("error", "1.0.0", null)]
    public void OnlyNewerVersionsAreUpdates(string installed, string latest, bool? expected) =>
        Assert.Equal(expected, RunnerReleaseCatalog.IsNewer(installed, latest));

    [Fact]
    public async Task ChecksAreScheduledIndependentlyAndLateObservationsCannotUndoAnUpdate()
    {
        var cache = new RunnerUpdateCache(); var id = Guid.NewGuid(); var now = DateTime.UtcNow;
        Assert.Null(cache.Get(id));
        var first = cache.Begin(id, now)!;
        Assert.True(first.Software && first.System);
        Assert.Null(cache.Begin(id, now)); // no concurrent check
        var observed = new RunnerUpdatesDto([new("codex", "Codex", "1.0.0", "1.1.0", true)], now, SystemPackageCount: 6, SystemCheckedAt: now);
        cache.Publish(id, first, observed);
        Assert.Equal(1, cache.Get(id)!.SoftwareCount);
        Assert.True(cache.Get(id)!.Checking);
        Assert.Null(cache.Begin(id, now));
        cache.Complete(id, first, observed, now);
        Assert.Null(cache.Begin(id, now.AddMinutes(59)));
        var hourly = cache.Begin(id, now.AddHours(1))!;
        Assert.True(hourly.Software); Assert.False(hourly.System);
        var maintenance = cache.WaitForCheckAsync(id, TestContext.Current.CancellationToken);
        Assert.False(maintenance.IsCompleted);
        cache.Request(id); // a maintenance operation finished while the hourly check was in flight
        cache.Complete(id, hourly, observed with { SystemPackageCount = 99 }, now.AddHours(1));
        await maintenance;
        Assert.Equal(6, cache.Get(id)!.SystemPackageCount);
        var again = cache.Begin(id, now.AddHours(1))!;
        Assert.True(again.Software && again.System);
        cache.Complete(id, again, observed with { SystemPackageCount = 0 }, now.AddHours(1));
        Assert.False(cache.Get(id)!.Checking);
        cache.RemoveMissing([]);
        Assert.Null(cache.Get(id));
    }

    [Fact]
    public void FailuresKeepTheLastObservationAndRetrySoonerWithoutReportingUpToDate()
    {
        var cache = new RunnerUpdateCache(); var id = Guid.NewGuid(); var now = DateTime.UtcNow;
        var ticket = cache.Begin(id, now)!;
        var observed = new RunnerUpdatesDto([new("codex", "Codex", "1.0.0", "1.1.0", true)], now,
            SoftwareError: "Offline", SystemPackageCount: 6, SystemCheckedAt: now, SystemError: "Offline");
        cache.Complete(id, ticket, observed, now);
        Assert.False(cache.Get(id)!.SoftwareKnown);
        Assert.Equal(1, cache.Get(id)!.SoftwareCount);
        Assert.Equal(6, cache.Get(id)!.SystemPackageCount);
        Assert.Null(cache.Begin(id, now.AddMinutes(9)));
        Assert.NotNull(cache.Begin(id, now.AddMinutes(10)));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("DAM_PACKAGES=-1\nDAM_REBOOT=0", false)]
    [InlineData("DAM_PACKAGES=0\n", false)]
    [InlineData("DAM_PACKAGES=0\nDAM_PACKAGES=1\nDAM_REBOOT=0", false)]
    [InlineData("DAM_PACKAGES=6\nDAM_REBOOT=1", true)]
    public void IncompletePackageChecksNeverBecomeZero(string output, bool valid)
    {
        if (valid) Assert.Equal((6, true), RunnerUpdateMonitor.ParseSystem(output));
        else Assert.Throws<FormatException>(() => RunnerUpdateMonitor.ParseSystem(output));
    }

    [Fact]
    public void RestartStatusReportsKernelsAndUnknownPartsKeepTheLastObservation()
    {
        const string output = "1.2.3\nDAM_REBOOT=1\nDAM_KERNEL_RUNNING=6.12.113+deb13-amd64\nDAM_KERNEL_INSTALLED=6.12.116+deb13-amd64\nDAM_KERNEL_AVAILABLE=6.12.117+deb13-amd64\n";
        var (reboot, kernel) = RunnerUpdateMonitor.ParseRestart(output);
        Assert.True(reboot);
        Assert.Equal(new RunnerKernelDto("6.12.113+deb13-amd64", "6.12.116+deb13-amd64", "6.12.117+deb13-amd64"), kernel);
        Assert.True(new RunnerUpdatesDto([], Kernel: kernel).RestartPending);
        var current = RunnerUpdateMonitor.ParseRestart("DAM_REBOOT=0\nDAM_KERNEL_RUNNING=6.12.116+deb13-amd64\nDAM_KERNEL_INSTALLED=\nDAM_KERNEL_AVAILABLE=\r\n");
        Assert.Equal((false, new RunnerKernelDto("6.12.116+deb13-amd64")), current);
        Assert.False(new RunnerUpdatesDto([], Kernel: current.Kernel).RestartPending);
        Assert.True(new RunnerUpdatesDto([], RebootRequired: true, Kernel: current.Kernel).RestartPending);
        // Older scripts, missing or malformed lines: unknown, so callers keep the previous observation.
        Assert.Equal((null, null), RunnerUpdateMonitor.ParseRestart("DAM_PACKAGES=0\n"));
        Assert.Null(RunnerUpdateMonitor.ParseRestart("DAM_KERNEL_RUNNING=6.12.116+deb13-amd64\nDAM_KERNEL_INSTALLED=\n").Kernel);
        Assert.Null(RunnerUpdateMonitor.ParseRestart("DAM_KERNEL_RUNNING=\nDAM_KERNEL_INSTALLED=\nDAM_KERNEL_AVAILABLE=\n").Kernel);
        Assert.Null(RunnerUpdateMonitor.ParseRestart(output.Replace("INSTALLED=6.12.116+deb13-amd64", "INSTALLED=6.12.113+deb13-amd64")).Kernel);
        Assert.Null(RunnerUpdateMonitor.ParseRestart(output.Replace("AVAILABLE=6.12.117+deb13-amd64", "AVAILABLE=6.12.116+deb13-amd64")).Kernel);
        Assert.Null(RunnerUpdateMonitor.ParseRestart(output.Replace("6.12.117+deb13-amd64", "6.12.117 amd64")).Kernel);
        Assert.Null(RunnerUpdateMonitor.ParseRestart(output + "DAM_KERNEL_RUNNING=6.12.116+deb13-amd64\n").Kernel);
        Assert.Null(RunnerUpdateMonitor.ParseRestart(output.Replace("DAM_REBOOT=1", "DAM_REBOOT=yes")).Reboot);
    }

    [Fact]
    public void PackageDetailsAreBoundedAndIncompleteDetailsNeverReplaceSavedResults()
    {
        const string output = "DAM_PACKAGE\tlibexample:amd64\t1.0\t2.0\nDAM_PACKAGE\tnew-dependency\t\t1.2\nDAM_PACKAGES=2\nDAM_PACKAGE_DETAILS=1\nDAM_REBOOT=0\n";
        var packages = RunnerUpdateMonitor.ParseSystemPackages(output, 2)!;
        Assert.Equal(new("libexample:amd64", "1.0", "2.0"), packages[0]);
        Assert.Equal(new("new-dependency", null, "1.2"), packages[1]);
        Assert.Null(RunnerUpdateMonitor.ParseSystemPackages("DAM_PACKAGES=2\nDAM_REBOOT=0", 2));
        Assert.Empty(RunnerUpdateMonitor.ParseSystemPackages("DAM_PACKAGE_DETAILS=1\n", 0)!);
        Assert.Throws<FormatException>(() => RunnerUpdateMonitor.ParseSystemPackages(output, 3));
        Assert.Throws<FormatException>(() => RunnerUpdateMonitor.ParseSystemPackages(output.Replace("\t2.0", "\t"), 2));
        Assert.Throws<FormatException>(() => RunnerUpdateMonitor.ParseSystemPackages(output.Replace("new-dependency", "libexample:amd64"), 2));
        Assert.Throws<FormatException>(() => RunnerUpdateMonitor.ParseSystemPackages(output + "DAM_PACKAGE_DETAILS=1\n", 2));
        var bounded = string.Join('\n', Enumerable.Range(0, RunnerUpdatesDto.MaxSystemPackageDetails).Select(i => $"DAM_PACKAGE\tpackage-{i}\t1.0\t2.0")) + "\nDAM_PACKAGE_DETAILS=1\n";
        Assert.Equal(RunnerUpdatesDto.MaxSystemPackageDetails, RunnerUpdateMonitor.ParseSystemPackages(bounded, 500)!.Count);
        Assert.Throws<FormatException>(() => RunnerUpdateMonitor.ParseSystemPackages(bounded + "DAM_PACKAGE\textra\t1.0\t2.0\n", 500));
    }

    [Fact]
    public async Task ReleaseLookupsAreSharedAndAnOutageKeepsTheLastVersionButMarksItUnknown()
    {
        var clock = new Clock(); var handler = new Registry(); using var client = new HttpClient(handler);
        var catalog = new RunnerReleaseCatalog(clock, client); var ct = TestContext.Current.CancellationToken;
        Assert.Equal("1.2.0", (await catalog.LatestAsync("@example/tool", false, ct)).Version);
        Assert.Equal("1.2.0", (await catalog.LatestAsync("@example/tool", false, ct)).Version);
        Assert.Equal(1, handler.Calls);
        clock.Now = clock.Now.AddHours(1); handler.Fail = true;
        var failed = await catalog.LatestAsync("@example/tool", false, ct);
        Assert.Equal("1.2.0", failed.Version); Assert.NotNull(failed.Error);
        await catalog.LatestAsync("@example/tool", false, ct);
        Assert.Equal(2, handler.Calls);
        clock.Now = clock.Now.AddMinutes(10); handler.Fail = false;
        Assert.Null((await catalog.LatestAsync("@example/tool", false, ct)).Error);
        Assert.Equal(3, handler.Calls);
        Assert.Equal("2.0.0", (await catalog.LatestAsync("example/editor", true, ct)).Version);
    }
    [Fact]
    public async Task ManualChecksRefreshReleaseMetadataWithoutBurstingTheRegistry()
    {
        var clock = new Clock(); var handler = new Registry(); using var client = new HttpClient(handler);
        var catalog = new RunnerReleaseCatalog(clock, client); var ct = TestContext.Current.CancellationToken;
        await catalog.LatestAsync("@example/tool", false, ct);
        await catalog.LatestAsync("@example/tool", false, ct, refresh: true);
        Assert.Equal(1, handler.Calls);
        clock.Now = clock.Now.AddMinutes(2); handler.Fail = true;
        Assert.NotNull((await catalog.LatestAsync("@example/tool", false, ct, refresh: true)).Error);
        clock.Now = clock.Now.AddMinutes(2); handler.Fail = false;
        Assert.Null((await catalog.LatestAsync("@example/tool", false, ct, refresh: true)).Error);
        Assert.Equal(3, handler.Calls);
    }

    [Fact(Timeout = 60_000)]
    public async Task SystemCheckOverSshOnlyRefreshesListsAndSimulatesAndRejectsRepositoryFailures()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.SkipWhen(!File.Exists("/usr/sbin/sshd"), "Requires the disposable SSH test server.");
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await RunnerShellIntegrationTests.Fixture.StartAsync();
        var bin = Directory.CreateDirectory(fixture.Root + "/bin").FullName;
        var calls = fixture.Root + "/apt-calls";
        var failed = fixture.Root + "/fail";
        var scripts = new Dictionary<string, string>
        {
            ["id"] = "#!/bin/sh\necho 1000\n",
            ["sudo"] = "#!/bin/sh\n[ \"$1\" = -n ] && shift\nexec \"$@\"\n",
            ["apt-get"] = $"#!/bin/sh\nprintf '%s\\n' \"$*\" >> '{calls}'\n[ ! -f '{failed}' ] || exit 42\nif [ \"$*\" = '-s dist-upgrade' ]; then printf 'Inst existing [1.0] (2.0)\\nInst new-dependency (1.0)\\nInst linux-image-6.12.117+deb13-amd64 (6.12.117-1 Debian-Security:13/stable-security [amd64])\\nInst linux-image-6.12.117+deb13-amd64-dbg (6.12.117-1)\\nInst linux-image-amd64 [6.12.116-1] (6.12.117-1)\\nRemv old-package [1.0]\\n'; fi\n",
            // The running kernel is older than one installed by unattended-upgrades; the simulation installs a newer one.
            ["uname"] = "#!/bin/sh\nif [ \"$1\" = -r ]; then echo 6.12.113+deb13-amd64; else exec /usr/bin/uname \"$@\"; fi\n",
            ["linux-version"] = "#!/bin/sh\ncase \"$1\" in list) printf '6.12.113+deb13-amd64\\n6.12.116+deb13-amd64\\n' ;; sort) sort -V ;; *) exit 2 ;; esac\n"
        };
        foreach (var (name, script) in scripts)
        {
            var path = Path.Combine(bin, name);
            await File.WriteAllTextAsync(path, script, ct);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var pathPrefix = $"export PATH={ShellQuote.Quote(bin)}:$PATH\n";
        var command = pathPrefix + RunnerSetupService.SystemCheckScript;
        var result = await fixture.Shell.RunAsync(fixture.Runner, command, cancellationToken: ct);
        Assert.True(result.Succeeded, result.Combined);
        Assert.Equal(5, RunnerUpdateMonitor.ParseSystem(result.Output).Count);
        Assert.Equal(new RunnerKernelDto("6.12.113+deb13-amd64", "6.12.116+deb13-amd64", "6.12.117+deb13-amd64"), RunnerUpdateMonitor.ParseRestart(result.Output).Kernel);
        // The hourly check passes the last available kernel back: dropped once installed, kept while still newer.
        async Task<RunnerKernelDto?> Hourly(string arguments) => RunnerUpdateMonitor.ParseRestart((await fixture.Shell.RunAsync(fixture.Runner,
            pathPrefix + RunnerSetupService.RestartStatusScript + "\nset -o pipefail\ndam_restart_status " + arguments, cancellationToken: ct)).EnsureSuccess("Restart status").Output).Kernel;
        Assert.Equal(new RunnerKernelDto("6.12.113+deb13-amd64", "6.12.116+deb13-amd64"), await Hourly("6.12.116+deb13-amd64"));
        Assert.Equal(new RunnerKernelDto("6.12.113+deb13-amd64", "6.12.116+deb13-amd64"), await Hourly("6.12.110+deb13-amd64"));
        Assert.Equal(new RunnerKernelDto("6.12.113+deb13-amd64", "6.12.116+deb13-amd64", "6.12.117+deb13-amd64"), await Hourly("6.12.117+deb13-amd64"));
        Assert.Equal([new("existing", "1.0", "2.0"), new("new-dependency", null, "1.0")], RunnerUpdateMonitor.ParseSystemPackages(result.Output, 5)!.Take(2));
        var arguments = await File.ReadAllLinesAsync(calls, ct);
        Assert.Equal(2, arguments.Length);
        Assert.StartsWith("-q update ", arguments[0]);
        Assert.Equal("-s dist-upgrade", arguments[1]);
        await File.WriteAllTextAsync(failed, "fail", ct);
        result = await fixture.Shell.RunAsync(fixture.Runner, command, cancellationToken: ct);
        Assert.Equal(42, result.ExitCode);
        Assert.DoesNotContain("DAM_PACKAGES=", result.Output);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Registry : HttpMessageHandler
    {
        public int Calls; public bool Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(request.RequestUri!.Host == "api.github.com" ? "{\"tag_name\":\"v2.0.0\"}" : "{\"latest\":\"1.2.0\"}") });
        }
    }
}
