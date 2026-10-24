using System.Diagnostics;
using System.Text.Json;
using DeveAgentManager.Core.Accounts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Harnesses.Claude;
using DeveAgentManager.Harnesses.Codex;
using DeveAgentManager.Harnesses.Grok;
using DeveAgentManager.Harnesses.Pi;
using Xunit;

namespace DeveAgentManager.Tests;

public class AccountRuntimeTests
{
    [Fact]
    public async Task PiWorkspacesKeepSettingsAndConversationFilesIndependent()
    {
        if (OperatingSystem.IsWindows()) return;
        var temp = Directory.CreateTempSubdirectory("dam-pi-workspace-");
        try
        {
            var home = temp.FullName;
            var shared = Path.Combine(home, ".pi", "agent");
            Directory.CreateDirectory(Path.Combine(shared, "sessions", "test"));
            await File.WriteAllTextAsync(Path.Combine(shared, "settings.json"), "{\"model\":\"initial\"}", TestContext.Current.CancellationToken);
            var agents = Enumerable.Range(0, 2).Select(_ => new Agent { Id = Guid.NewGuid(), OwnerId = Guid.NewGuid(), HarnessId = "pi", HarnessSessionId = Guid.NewGuid().ToString("N") }).ToArray();
            foreach (var agent in agents)
            {
                await File.WriteAllTextAsync(Path.Combine(shared, "sessions", "test", "time_" + agent.HarnessSessionId + ".jsonl"), agent.Id.ToString(), TestContext.Current.CancellationToken);
                await Run(AccountService.PiWorkspaceScript(agent).Replace("pathlib.Path.home()", "pathlib.Path(" + JsonSerializer.Serialize(home) + ")"));
                var runtime = AccountService.RuntimeFolder(agent).Replace("$HOME", home);
                Assert.Single(Directory.EnumerateFiles(Path.Combine(runtime, "sessions"), "*.jsonl", SearchOption.AllDirectories));
                Assert.Null(new FileInfo(Path.Combine(runtime, "settings.json")).LinkTarget);
                var privateHistory = Path.Combine(runtime, "sessions", "test", "time_" + agent.HarnessSessionId + ".jsonl");
                await File.WriteAllTextAsync(privateHistory, "private updated history", TestContext.Current.CancellationToken);
                File.SetLastWriteTimeUtc(privateHistory, DateTime.UtcNow.AddMinutes(1));
                Assert.Contains(privateHistory, await Run(new PiSessionFormat().LocateCommand(agent.HarnessSessionId).Replace("$HOME", home)));
            }
            await File.WriteAllTextAsync(Path.Combine(AccountService.RuntimeFolder(agents[0]).Replace("$HOME", home), "settings.json"), "changed first", TestContext.Current.CancellationToken);
            Assert.Equal("{\"model\":\"initial\"}", await File.ReadAllTextAsync(Path.Combine(AccountService.RuntimeFolder(agents[1]).Replace("$HOME", home), "settings.json"), TestContext.Current.CancellationToken));
            Assert.Equal("{\"model\":\"initial\"}", await File.ReadAllTextAsync(Path.Combine(shared, "settings.json"), TestContext.Current.CancellationToken));
        }
        finally { temp.Delete(true); }
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("grok")]
    public async Task SharedCredentialDoesNotShareHistoryOrMutableRuntime(string harness)
    {
        if (OperatingSystem.IsWindows()) return;
        var temp = Directory.CreateTempSubdirectory("dam-login-runtime-");
        try
        {
            var home = temp.FullName;
            var account = Guid.NewGuid();
            var first = new Agent { Id = Guid.NewGuid(), OwnerId = Guid.NewGuid(), HarnessId = harness, HarnessSessionId = Guid.NewGuid().ToString(), IsWorktree = true };
            var second = new Agent { Id = Guid.NewGuid(), OwnerId = Guid.NewGuid(), HarnessId = harness, HarnessSessionId = Guid.NewGuid().ToString(), IsWorktree = true };
            string Relative(Agent a) => harness == "codex" ? $"sessions/2026/01/01/rollout-test-{a.HarnessSessionId}.jsonl" : harness == "grok" ? $"sessions/test/{a.HarnessSessionId}/updates.jsonl" : $"projects/test/{a.HarnessSessionId}.jsonl";
            var shared = Path.Combine(home, "." + harness);
            Directory.CreateDirectory(shared);
            var config = harness == "claude" ? "settings.json" : "config.toml";
            await File.WriteAllTextAsync(Path.Combine(shared, config), harness == "claude" ? "{}" : "# initial", TestContext.Current.CancellationToken);
            foreach (var a in new[] { first, second })
            {
                var file = Path.Combine(shared, Relative(a));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, a.Id.ToString(), TestContext.Current.CancellationToken);
            }
            var auth = Path.Combine(home, ".deveagentmanager", "accounts", account.ToString("N"), "codex", "auth.json");
            Directory.CreateDirectory(Path.GetDirectoryName(auth)!);
            await File.WriteAllTextAsync(auth, "test-only-access", TestContext.Current.CancellationToken);
            foreach (var a in new[] { first, second })
            {
                var script = AccountService.RuntimeScript(a, account)
                    .Replace("pathlib.Path.home()", "pathlib.Path(" + JsonSerializer.Serialize(home) + ")");
                await Run(script);
                var runtime = AccountService.RuntimeFolder(a).Replace("$HOME", home);
                Assert.Null(new FileInfo(Path.Combine(runtime, config)).LinkTarget);
                Assert.True(File.Exists(Path.Combine(runtime, Relative(a))));
                Assert.False(File.Exists(Path.Combine(runtime, Relative(a == first ? second : first))));
                Assert.Single(Directory.EnumerateFiles(Path.Combine(runtime, harness is "codex" or "grok" ? "sessions" : "projects"), "*.jsonl", SearchOption.AllDirectories));
                Assert.Null(new DirectoryInfo(Path.Combine(runtime, harness is "codex" or "grok" ? "sessions" : "projects")).LinkTarget);
                if (harness == "codex")
                {
                    Assert.NotNull(new FileInfo(Path.Combine(runtime, "auth.json")).LinkTarget);
                    await File.WriteAllTextAsync(auth, "renewed-test-only-access", TestContext.Current.CancellationToken);
                    Assert.Equal("renewed-test-only-access", await File.ReadAllTextAsync(Path.Combine(runtime, "auth.json"), TestContext.Current.CancellationToken));
                }
                var locate = harness == "codex" ? new CodexSessionFormat().LocateCommand(a.HarnessSessionId) : harness == "grok" ? new GrokSessionFormat().LocateCommand(a.HarnessSessionId) : new ClaudeSessionFormat().LocateCommand(a.HarnessSessionId);
                Assert.Contains(a.HarnessSessionId, await Run(locate.Replace("$HOME", home)));
            }
            var secondSettings = await File.ReadAllTextAsync(Path.Combine(AccountService.RuntimeFolder(second).Replace("$HOME", home), config), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(AccountService.RuntimeFolder(first).Replace("$HOME", home), config), "changed first", TestContext.Current.CancellationToken);
            Assert.Equal(secondSettings, await File.ReadAllTextAsync(Path.Combine(AccountService.RuntimeFolder(second).Replace("$HOME", home), config), TestContext.Current.CancellationToken));
        }
        finally { temp.Delete(true); }
    }

    private static async Task<string> Run(string script)
    {
        using var process = Process.Start(new ProcessStartInfo("bash", ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
}
