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
            ["apt-get"] = $"#!/bin/sh\nprintf '%s\\n' \"$*\" >> '{calls}'\n[ ! -f '{failed}' ] || exit 42\nif [ \"$*\" = '-s dist-upgrade' ]; then printf 'Inst existing [1.0] (2.0)\\nInst new-dependency (1.0)\\nRemv old-package [1.0]\\n'; fi\n"
        };
        foreach (var (name, script) in scripts)
        {
            var path = Path.Combine(bin, name);
            await File.WriteAllTextAsync(path, script, ct);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var command = $"export PATH={ShellQuote.Quote(bin)}:$PATH\n" + RunnerSetupService.ReadResource("check-system-updates.sh");
        var result = await fixture.Shell.RunAsync(fixture.Runner, command, cancellationToken: ct);
        Assert.True(result.Succeeded, result.Combined);
        Assert.Equal(2, RunnerUpdateMonitor.ParseSystem(result.Output).Count);
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
