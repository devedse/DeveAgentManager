using System.Diagnostics;
using System.Text.Json;
using DeveAgentManager.Core.Accounts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Harnesses.Claude;
using DeveAgentManager.Harnesses.Codex;
using Xunit;

namespace DeveAgentManager.Tests;

public class AccountRuntimeTests
{
    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public async Task SharedCredentialDoesNotShareHistoryOrMutableRuntime(string harness)
    {
        if (OperatingSystem.IsWindows()) return;
        var temp = Directory.CreateTempSubdirectory("dam-login-runtime-");
        try
        {
            var home = temp.FullName;
            var account = Guid.NewGuid();
            var first = new Agent { Id = Guid.NewGuid(), OwnerId = Guid.NewGuid(), HarnessId = harness, HarnessSessionId = Guid.NewGuid().ToString() };
            var second = new Agent { Id = Guid.NewGuid(), OwnerId = Guid.NewGuid(), HarnessId = harness, HarnessSessionId = Guid.NewGuid().ToString() };
            string Relative(Agent a) => harness == "codex" ? $"sessions/2026/01/01/rollout-test-{a.HarnessSessionId}.jsonl" : $"projects/test/{a.HarnessSessionId}.jsonl";
            var shared = Path.Combine(home, "." + harness);
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
                Assert.True(File.Exists(Path.Combine(runtime, Relative(a))));
                Assert.False(File.Exists(Path.Combine(runtime, Relative(a == first ? second : first))));
                Assert.Single(Directory.EnumerateFiles(Path.Combine(runtime, harness == "codex" ? "sessions" : "projects"), "*.jsonl", SearchOption.AllDirectories));
                Assert.Null(new DirectoryInfo(Path.Combine(runtime, harness == "codex" ? "sessions" : "projects")).LinkTarget);
                if (harness == "codex")
                {
                    Assert.NotNull(new FileInfo(Path.Combine(runtime, "auth.json")).LinkTarget);
                    await File.WriteAllTextAsync(auth, "renewed-test-only-access", TestContext.Current.CancellationToken);
                    Assert.Equal("renewed-test-only-access", await File.ReadAllTextAsync(Path.Combine(runtime, "auth.json"), TestContext.Current.CancellationToken));
                }
                var locate = harness == "codex" ? new CodexSessionFormat().LocateCommand(a.HarnessSessionId) : new ClaudeSessionFormat().LocateCommand(a.HarnessSessionId);
                Assert.Contains(a.HarnessSessionId, await Run(locate.Replace("$HOME", home)));
            }
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
