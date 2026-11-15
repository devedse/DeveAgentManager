using System.Diagnostics;
using DeveAgentManager.Core;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Security;
using DeveAgentManager.Core.Ssh;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// Creating the Runner Account on a container, where only root has the Manager's key, against an isolated sshd that runs
/// as the test user (so it can only log in that user: any other name stands for an account that doesn't exist).
/// </summary>
public sealed class RunnerAccountBootstrapTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string MissingAccount = "dam-test-no-such-account";

    [Fact(Timeout = 60_000)]
    public async Task AnExistingRunnerAccountIsLeftAlone()
    {
        Assert.SkipWhen(!File.Exists("/usr/sbin/sshd"), "Requires OpenSSH.");
        await using var ssh = await RunnerShellIntegrationTests.Fixture.StartAsync();
        await using var services = Services(ssh);
        var log = new Log();

        await services.GetRequiredService<RunnerSetupService>().CreateRunnerAccountAsync(ssh.Runner, MissingAccount, log, TimeSpan.FromSeconds(30), Ct);

        Assert.Contains(log.Lines, line => line.Contains("already exists"));
        Assert.DoesNotContain(log.Lines, line => line.Contains("Logging in as"));
    }

    [Fact(Timeout = 60_000)]
    public async Task AMissingAccountIsCreatedByLoggingInAsTheBootstrapUserOnce()
    {
        Assert.SkipWhen(!File.Exists("/usr/sbin/sshd"), "Requires OpenSSH.");
        // As root the account script would really create an account and a sudoers file on this machine.
        Assert.SkipWhen(Environment.UserName == "root", "Must not run as root.");
        await using var ssh = await RunnerShellIntegrationTests.Fixture.StartAsync();
        await using var services = Services(ssh);
        ssh.Runner.Username = MissingAccount;
        ssh.Runner.ProtectedPassword = services.GetRequiredService<SecretProtector>().Protect("test-only-password");
        var log = new Log();

        // The test user can't add accounts, so the script stops at useradd, before it changed anything.
        var error = await Assert.ThrowsAsync<RunnerCommandException>(() => services.GetRequiredService<RunnerSetupService>()
            .CreateRunnerAccountAsync(ssh.Runner, Environment.UserName, log, TimeSpan.FromSeconds(30), Ct));

        Assert.Contains($"as {Environment.UserName}", error.Message);
        Assert.Single(log.Lines, line => line.Contains($"Logging in as {Environment.UserName}"));
        Assert.True(ssh.Authentications >= 1);
    }

    [Fact(Timeout = 60_000)]
    public async Task SaysSoWhenNeitherAccountTakesTheKey()
    {
        Assert.SkipWhen(!File.Exists("/usr/sbin/sshd"), "Requires OpenSSH.");
        await using var ssh = await RunnerShellIntegrationTests.Fixture.StartAsync();
        await using var services = Services(ssh);
        ssh.Runner.Username = MissingAccount;
        ssh.Runner.ProtectedPassword = services.GetRequiredService<SecretProtector>().Protect("test-only-password");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => services.GetRequiredService<RunnerSetupService>()
            .CreateRunnerAccountAsync(ssh.Runner, "dam-test-no-such-root", new Log(), TimeSpan.FromSeconds(30), Ct));

        Assert.Contains("Neither", error.Message);
        Assert.Equal(0, ssh.CommandSessions);
    }

    [Fact]
    public async Task TheAccountScriptKeepsEveryCharacterAndIsValidBash()
    {
        const string password = "it's \"$HOME\" `id` & $(true) \\ x";
        const string key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAItestonly test-only key";
        var script = RunnerSetupService.BuildRunnerAccountScript("runner-account", password, key);
        Assert.Equal("", await BashAsync("-n", "-c", script));

        // Only the assignments run: they must give the values back exactly.
        var assignments = string.Join('\n', script.Split('\n').Where(l => l.StartsWith("user=") || l.StartsWith("key=") || l.StartsWith("password=")));
        Assert.Equal($"runner-account\n{key}\n{password}\n", await BashAsync("-c", assignments + "\nprintf '%s\\n' \"$user\" \"$key\" \"$password\""));
    }

    [Fact]
    public async Task RemovingTheKeyFromAnAccountThatDoesNotExistDoesNothing()
    {
        // Never touches a home it couldn't find (it would otherwise be /.ssh/authorized_keys).
        Assert.Equal("", await BashAsync("-c", "sudo() { echo called sudo; }\n" + RunnerSetupService.BuildRemoveBootstrapKeyScript(MissingAccount, "ssh-ed25519 test-only")));
    }

    private static ServiceProvider Services(RunnerShellIntegrationTests.Fixture ssh)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddDeveAgentManagerCore(new ManagerOptions { DataDirectory = ssh.Root + "/manager", AdminPassword = "test-only-password" });
        collection.AddSingleton(ssh.Shell);
        return collection.BuildServiceProvider();
    }

    private static async Task<string> BashAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("bash", arguments) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = await process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.True(process.ExitCode == 0, error);
        return await output;
    }

    private sealed class Log : IRunnerLog
    {
        public List<string> Lines { get; } = [];
        public void Write(RunnerLogLevel level, string text) { lock (Lines) Lines.Add(text); }
    }
}
