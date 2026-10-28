using System.Diagnostics;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Accounts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Setup;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Security;
using DeveAgentManager.Core.Users;
using DeveAgentManager.Harnesses.Pi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class PiManagedLoginTests(ApiSecurityTests.Manager manager) : IClassFixture<ApiSecurityTests.Manager>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task PiRequiresExplicitCompatibleGrantedLoginAndRevocationBlocksItsRunningBinding(string harness)
    {
        using var client = manager.CreateClient();
        var users = manager.Services.GetRequiredService<UserService>();
        var accounts = manager.Services.GetRequiredService<AccountService>();
        var factory = manager.Services.GetRequiredService<IDbContextFactory<ManagerDbContext>>();
        async Task<CurrentUser> NewUser() => CurrentUser.From(await users.CreateAsync(TestUsers.Admin,
            "u" + Guid.NewGuid().ToString("N")[..12], null, "test-only-password", UserRoles.None, Ct));
        var owner = await NewUser();
        var recipient = await NewUser();
        var login = await accounts.AddAsync(owner.Id, harness, "Test subscription", AccountKind.Subscription, "test-only-credential", Ct);
        Assert.Null(await accounts.ResolveAsync(new Agent { OwnerId = owner.Id, HarnessId = "pi" }, Ct));
        var agent = new Agent { Id = Guid.NewGuid(), OwnerId = recipient.Id, HarnessId = "pi", AccountId = login.Id };
        await Assert.ThrowsAsync<InvalidOperationException>(() => accounts.ResolveAsync(agent, Ct));
        await accounts.SetGrantAsync(owner, login.Id, recipient.Id, AccountAccess.Use, Ct);
        Assert.Equal(login.Id, (await accounts.ResolveAsync(agent, Ct))!.Id);
        var apiKey = await accounts.AddAsync(owner.Id, harness, "Test API key", AccountKind.ApiKey, "test-only-key", Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => accounts.ResolveAsync(new Agent { OwnerId = owner.Id, HarnessId = "pi", AccountId = apiKey.Id }, Ct));
        await using var db = await factory.CreateDbContextAsync(Ct);
        agent.Runner = new Runner { Name = "test-" + Guid.NewGuid().ToString("N"), OwnerId = owner.Id, State = RunnerState.Stopped };
        agent.Name = "pi-preview";
        agent.State = AgentState.Running;
        agent.RunningAccountId = login.Id;
        db.Agents.Add(agent);
        await db.SaveChangesAsync(Ct);
        Assert.Contains((await accounts.ImpactAsync(owner, login.Id, recipient.Id, Ct)).Agents, a => a.Running);
        await accounts.SetGrantAsync(owner, login.Id, recipient.Id, null, Ct);
        db.ChangeTracker.Clear();
        var blocked = await db.Agents.SingleAsync(a => a.Id == agent.Id, Ct);
        Assert.True(blocked.LoginBlocked);
        Assert.True(blocked.LoginStopPending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => accounts.ResolveAsync(blocked, Ct));
    }

    [Fact]
    public async Task PiSwitchChecksBothActorAndOwnerBeforeStoppingAndRejectsApiKeys()
    {
        using var client = manager.CreateClient();
        var users = manager.Services.GetRequiredService<UserService>();
        var accounts = manager.Services.GetRequiredService<AccountService>();
        var factory = manager.Services.GetRequiredService<IDbContextFactory<ManagerDbContext>>();
        async Task<CurrentUser> NewUser() => CurrentUser.From(await users.CreateAsync(TestUsers.Admin,
            "u" + Guid.NewGuid().ToString("N")[..12], null, "test-only-password", UserRoles.None, Ct));
        var actor = await NewUser(); var owner = await NewUser(); var other = await NewUser();
        var login = await accounts.AddAsync(actor.Id, "claude", "Test-only subscription", AccountKind.Subscription, "test-only-token", Ct);
        var runner = new Runner { Name = "test-" + Guid.NewGuid().ToString("N"), OwnerId = owner.Id, State = RunnerState.Stopped };
        var agent = new Agent { Id = Guid.NewGuid(), Runner = runner, OwnerId = owner.Id, Name = "test-pi", HarnessId = "pi", State = AgentState.Running };
        await using var db = await factory.CreateDbContextAsync(Ct);
        db.Agents.Add(agent); await db.SaveChangesAsync(Ct);
        var service = manager.Services.GetRequiredService<DeveAgentManager.Core.Agents.AgentService>();
        await Assert.ThrowsAsync<ArgumentException>(() => service.SwitchAsync(actor, agent.Id, "pi", login.Id, Ct));
        await accounts.SetGrantAsync(actor, login.Id, owner.Id, AccountAccess.Use, Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SwitchAsync(other, agent.Id, "pi", login.Id, Ct));
        var key = await accounts.AddAsync(actor.Id, "claude", "Test-only key", AccountKind.ApiKey, "test-only-key", Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SwitchAsync(actor, agent.Id, "pi", key.Id, Ct));
        var privateLogin = await accounts.AddAsync(owner.Id, "claude", "Owner-only test login", AccountKind.Subscription, "test-only-token", Ct);
        await manager.Services.GetRequiredService<UserSettingsStore>().SaveModelDefaultsAsync(owner.Id,
            new Dictionary<string, ModelDefault> { ["pi"] = new() { PiAccountIds = [login.Id, privateLogin.Id] } }, Ct);
        // Both null and legacy explicit selections must authorize every account that will actually be forwarded.
        await Assert.ThrowsAsync<ArgumentException>(() => service.SwitchAsync(actor, agent.Id, "pi", null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SwitchAsync(actor, agent.Id, "pi", login.Id, Ct));
        db.ChangeTracker.Clear();
        var unchanged = await db.Agents.SingleAsync(a => a.Id == agent.Id, Ct);
        Assert.Equal(AgentState.Running, unchanged.State);
        Assert.Null(unchanged.AccountId);
    }

    [Fact]
    public async Task ManagedClaudeUsesPrivateRuntimeAndReturningToPiLoginsKeepsTheSameHistoryOverSsh()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/sshd"), "Requires an isolated OpenSSH server.");
        await using var ssh = await RunnerShellIntegrationTests.Fixture.StartAsync();
        using var client = manager.CreateClient();
        var accountService = manager.Services.GetRequiredService<AccountService>();
        var account = await accountService.AddAsync(User.BootstrapId, "claude", "Test-only pi login", AccountKind.Subscription, "test-only-oauth-token", Ct);
        var helper = new AccountService(manager.Services.GetRequiredService<IDbContextFactory<ManagerDbContext>>(),
            manager.Services.GetRequiredService<SecretProtector>(), ssh.Shell,
            [new DeveAgentManager.Harnesses.Claude.ClaudeHarness(), new PiHarness()], new ManagerEvents(), NullLogger<AccountService>.Instance);
        var home = ssh.Root + "/home";
        var agent = new Agent { Id = Guid.NewGuid(), OwnerId = User.BootstrapId, HarnessId = "pi", HarnessSessionId = Guid.NewGuid().ToString("N"), WorkingDirectory = home + "/work/test" };
        var shared = Path.Combine(home, ".pi/agent");
        Directory.CreateDirectory(shared + "/sessions/test");
        Directory.CreateDirectory(shared + "/git/test-package");
        await File.WriteAllTextAsync(shared + "/settings.json", "{}", Ct);
        await File.WriteAllTextAsync(shared + "/sessions/test/time_" + agent.HarnessSessionId + ".jsonl", "test-only native history\n", Ct);
        Directory.CreateDirectory(home + "/bin");
        var claude = home + "/bin/claude";
        await File.WriteAllTextAsync(claude, "#!/bin/sh\nexit 0\n", Ct);
        File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await helper.EnsureOnRunnerAsync(ssh.Runner, account, Ct);
        var environment = await helper.PreparePiManagedRuntimeAsync(ssh.Runner, agent, account, Ct);
        var check = "python3 -c 'import os,json; print(json.dumps({\"token_present\":bool(os.getenv(\"CLAUDE_CODE_OAUTH_TOKEN\")),\"pi\":os.getenv(\"PI_CODING_AGENT_DIR\"),\"claude\":os.getenv(\"CLAUDE_CONFIG_DIR\")}))'";
        var result = await ssh.Shell.RunAsync(ssh.Runner, $"export PATH=\"{home}/bin:$PATH\"; . \"{AccountService.EnvironmentFile(account.Id)}\"; {environment}{check}", cancellationToken: Ct);
        result.EnsureSuccess("Checking the isolated pi login");
        using var state = System.Text.Json.JsonDocument.Parse(result.Output);
        var runtime = AccountService.RuntimeFolder(agent).Replace("$HOME", home);
        Assert.True(state.RootElement.GetProperty("token_present").GetBoolean());
        Assert.Equal(runtime, state.RootElement.GetProperty("pi").GetString());
        Assert.Equal(runtime + "/claude-code", state.RootElement.GetProperty("claude").GetString());
        Assert.Equal("test-only native history\n", await File.ReadAllTextAsync(runtime + "/sessions/test/time_" + agent.HarnessSessionId + ".jsonl", Ct));
        Assert.Equal(AccountService.PiManagedExtension, await File.ReadAllTextAsync(runtime + "/dam-managed-login.ts", Ct));
        var back = await ssh.Shell.RunAsync(ssh.Runner, AccountService.ExistingPiRuntimeEnvironment(agent) + check, cancellationToken: Ct);
        back.EnsureSuccess("Returning to pi's own login");
        using var restored = System.Text.Json.JsonDocument.Parse(back.Output);
        Assert.False(restored.RootElement.GetProperty("token_present").GetBoolean());
        Assert.Equal(runtime, restored.RootElement.GetProperty("pi").GetString());
        Assert.False(File.Exists(runtime + "/claude-code/.credentials.json"));
        var second = await accountService.AddAsync(User.BootstrapId, "claude", "Second test-only pi login", AccountKind.Subscription, "test-only-second-oauth", Ct);
        await helper.EnsureOnRunnerAsync(ssh.Runner, second, Ct);
        await File.WriteAllTextAsync(claude, "#!/bin/sh\nprintf '%s|%s' \"$CLAUDE_CODE_OAUTH_TOKEN\" \"$CLAUDE_CONFIG_DIR\"\n", Ct);
        var multiple = await helper.PreparePiManagedRuntimeAsync(ssh.Runner, agent, new[] { account, second }, Ct);
        var noAmbient = await ssh.Shell.RunAsync(ssh.Runner, $"export PATH=\"{home}/bin:$PATH\"; " + multiple + check, cancellationToken: Ct);
        using var piState = System.Text.Json.JsonDocument.Parse(noAmbient.Output);
        Assert.False(piState.RootElement.GetProperty("token_present").GetBoolean());
        foreach (var login in new[] { account, second })
        {
            var wrapper = runtime + "/claude-" + login.Id.ToString("N") + ".sh";
            var selected = await ssh.Shell.RunAsync(ssh.Runner, $"export PATH=\"{home}/bin:$PATH\"; export CLAUDE_CODE_OAUTH_TOKEN=wrong-account; \"{wrapper}\"", cancellationToken: Ct);
            selected.EnsureSuccess("Checking a forwarded Claude wrapper");
            Assert.Equal((login.Id == account.Id ? "test-only-oauth-token" : "test-only-second-oauth") + "|" + runtime + "/claude-" + login.Id.ToString("N"), selected.Output);
            Assert.False(File.Exists(runtime + "/claude-" + login.Id.ToString("N") + "/.credentials.json"));
        }
        var manifest = await File.ReadAllTextAsync(runtime + "/dam-logins.json", Ct);
        Assert.DoesNotContain("test-only-oauth", manifest);
        Assert.Contains(PiManagedLogin.AccountProvider("claude", account.Id), manifest);
        Assert.Contains(PiManagedLogin.AccountProvider("claude", second.Id), manifest);

    }

    [Fact]
    public void ModelScopePreservesInheritanceAllOrderAndShellQuoting()
    {
        Assert.True(new ModelDefault().IsEmpty);
        Assert.False(new ModelDefault { ScopedModels = [] }.IsEmpty);
        var mine = new ModelDefault { ScopedModels = [" claude-code/*:high ", "dam-codex/*", "claude-code/*:high"] }.Cleaned();
        Assert.Equal(["claude-code/*:high", "dam-codex/*"], mine.ScopedModels);
        var settings = new HarnessConfig { ScopedModels = [], Settings = "{\"enabledModels\":[\"old/*\"]}" }.BuildSettings();
        Assert.Empty(settings["enabledModels"]!.AsArray());
        Assert.NotNull(PiModelScope.Problem(["one two"]));
        Assert.NotNull(PiModelScope.Problem(["one,two"]));
        Assert.NotNull(PiModelScope.Problem(Enumerable.Repeat("x", 65).ToList()));
        var harness = new PiHarness();
        var launch = new DeveAgentManager.Core.Abstractions.HarnessLaunch("test-session", "Test", "/tmp/test-workspace", ScopedModels: mine.ScopedModels);
        Assert.Contains("--models 'claude-code/*:high,dam-codex/*'", harness.BuildLaunchCommand(launch));
        Assert.Contains("--models 'claude-code/*:high,dam-codex/*'", harness.BuildLaunchCommand(launch with { ScopedModels = [" claude-code/*:high ", "dam-codex/*", "dam-codex/*"] }));
        Assert.Contains("--models '*'", harness.BuildLaunchCommand(launch with { ScopedModels = [] }));
        Assert.DoesNotContain("--models", harness.BuildLaunchCommand(launch with { ScopedModels = null }));
        Assert.DoesNotContain("--provider", harness.BuildLaunchCommand(launch with { ManagedLoginProvider = "dam-codex", Provider = "github-copilot", Model = "claude-example" }));
        Assert.Contains("--provider dam-codex", harness.BuildLaunchCommand(launch with { ManagedLoginProvider = "dam-codex", Model = "dam-codex/test-model" }));
    }

    [Fact]
    public async Task AdapterReadsRenewedAccessTokensWithoutOwningRefreshOrLoggingCredentials()
    {
        var temp = Directory.CreateTempSubdirectory("dam-pi-adapter-");
        try
        {
            var source = Path.Combine(temp.FullName, "pi-managed-login.ts");
            await File.WriteAllTextAsync(source, AccountService.PiManagedExtension, Ct);
            var fixture = Path.Combine(AppContext.BaseDirectory, "Js", "pi-managed-login.cjs");
            var start = new ProcessStartInfo("node", [fixture, source]) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var key in start.Environment.Keys.Where(key => key is not ("PATH" or "HOME" or "LANG" or "TMPDIR" or "SystemRoot")).ToList())
                start.Environment.Remove(key);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(Ct);
            var error = process.StandardError.ReadToEndAsync(Ct);
            await process.WaitForExitAsync(Ct);
            Assert.True(process.ExitCode == 0, await error);
            Assert.DoesNotContain("test-only-access", await output);
        }
        finally { temp.Delete(true); }
    }
}
