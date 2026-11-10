using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Ssh;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// Pulling for a workspace (Pull first, and updating before a new conversation) uses the owner's own git config and
/// GitHub token. The shared Runner settings hold no credentials, so a private repository refuses anything else, and that
/// refusal is explained instead of git asking for a username on a terminal that isn't there.
/// </summary>
public sealed class WorkspaceGitCredentialsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 60_000)]
    public async Task WorkspacePullsUseTheOwnersGitConfigAndExplainARefusedLogin()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Requires bash and git.");
        var root = Directory.CreateTempSubdirectory("dam-workspace-git-").FullName;
        // Stands in for a private GitHub repository: every request without credentials gets 401.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    context.Response.StatusCode = 401;
                    context.Response.AddHeader("WWW-Authenticate", "Basic realm=\"test-only\"");
                    context.Response.Close();
                }
                catch (Exception) { return; }
            }
        });
        try
        {
            var home = root + "/home";
            var bare = root + "/remote.git";
            var main = home + "/work/app";
            var other = root + "/other";
            var url = $"http://127.0.0.1:{port}/private.git";
            var owner = Guid.NewGuid();
            const string commit = "git -c user.name=Test -c user.email=test@example.com commit -q";
            await RunAsync(home, $"git init -q --bare -b main {Q(bare)} && git clone -q {Q(bare)} {Q(main)} && cd {Q(main)} && echo one > app.txt"
                + $" && git add app.txt && {commit} -m one && git push -q origin main && git remote set-url origin {Q(url)}"
                + $" && git clone -q {Q(bare)} {Q(other)} && cd {Q(other)} && echo two > app.txt && {commit} -am two && git push -q origin main");
            // The owner's own git config can reach the repository, as their GitHub token would.
            var folder = $"{home}/.deveagentmanager/users/{owner:N}";
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(folder + "/gitconfig", $"[url \"file://{bare}\"]\n\tinsteadOf = {url}\n", Ct);
            var container = AgentService.WorkspaceContainer(main);

            async Task<AgentService.WorkspacePlan> PlanAsync(string name)
            {
                var token = Guid.NewGuid().ToString("N");
                var (_, output, _) = await RunAsync(home, AgentService.WorkspaceCommand("preflight", new { Main = main, Source = main, Container = container,
                    Start = "MainBranch", Pull = true, NewBranch = (string?)null, Names = new[] { name }, Token = token }));
                var p = JsonDocument.Parse(output.Trim().Split('\n')[^1]).RootElement;
                string? Text(string field) => p.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                return new(main, main, container, Text("Path")!, name, WorkspaceStart.MainBranch, true, Text("Repo")!, Text("Branch"), null,
                    Text("Remote"), Text("Upstream"), Text("Commit")!, token);
            }

            // Without the owner's config (as before this fix): refused with a reason, and no folder is left behind.
            var refused = await PlanAsync("without-token");
            var (code, _, error) = await RunAsync(home, AgentService.WorkspaceCommand("create", refused), check: false);
            Assert.Equal(3, code);
            Assert.Contains("The remote repository refused access", error);
            Assert.Contains("turn off Pull first", error);
            Assert.DoesNotContain("No such device or address", error);
            Assert.False(Directory.Exists(refused.Path));
            Assert.Empty(Directory.GetDirectories(container));

            // With it: the workspace starts at the remote's newest commit, not Main's older copy.
            var plan = await PlanAsync("with-token");
            await RunAsync(home, AgentService.WorkspaceCommand("create", plan, gitUser: owner));
            Assert.Equal(await HeadAsync(home, bare), await HeadAsync(home, plan.Path));
            Assert.Equal("two\n", await File.ReadAllTextAsync(plan.Path + "/app.txt", Ct));

            // Updating before a new conversation pulls with the same config.
            await RunAsync(home, $"cd {Q(other)} && echo three > app.txt && {commit} -am three && git push -q origin main");
            (code, _, error) = await RunAsync(home, AgentService.WorkspaceCommand("pull", new { plan.Path }), check: false);
            Assert.Equal(3, code);
            Assert.Contains("The remote repository refused access", error);
            Assert.DoesNotContain("turn off Pull first", error);
            await RunAsync(home, AgentService.WorkspaceCommand("pull", new { plan.Path }, gitUser: owner));
            Assert.Equal("three\n", await File.ReadAllTextAsync(plan.Path + "/app.txt", Ct));
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Q(string value) => ShellQuote.Quote(value);

    private static async Task<string> HeadAsync(string home, string repo) =>
        (await RunAsync(home, $"git -C {Q(repo)} rev-parse HEAD")).Output.Trim();

    private static async Task<(int Code, string Output, string Error)> RunAsync(string home, string script, bool check = true)
    {
        var start = new ProcessStartInfo("bash", ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["HOME"] = home;
        // Like the Runner account's shell for these steps: no personal git config or token unless the step sets one.
        start.Environment.Remove("GIT_CONFIG_GLOBAL");
        start.Environment.Remove("GH_TOKEN");
        Directory.CreateDirectory(home);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        if (check) Assert.True(process.ExitCode == 0, await error);
        return (process.ExitCode, await output, await error);
    }
}
