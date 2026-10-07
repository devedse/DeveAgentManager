using System.Diagnostics;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Setup;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// What each person with access has on a Runner (RunnerUsers): their environment, their git settings with only their own
/// credentials, and their SSH keys in a managed part of authorized_keys. Checked with real bash and git where available.
/// </summary>
public sealed class RunnerUsersTests : IDisposable
{
    private static readonly Guid UserId = new("6f1c5c2e-8b7d-4a59-9e0f-2a1b3c4d5e6f");
    private const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGcWbW5Um3OldSwXFQ2PyjmHd9lU0w3tqxRrW+yN1kFH ketty@laptop";

    private readonly string _home = Directory.CreateTempSubdirectory("dam-runner-users-").FullName;

    [Fact]
    public void Environment_QuotesValues_SkipsInvalidAndReservedNames_AndSetsGitAndToken()
    {
        var env = RunnerUsers.BuildEnvironment(UserId, "ketty", new UserProfile
        {
            EnvironmentVariables =
            [
                new SetupEnvironmentVariable { Name = "ANTHROPIC_API_KEY", Value = "sk-'x' y" },
                new SetupEnvironmentVariable { Name = "BAD NAME", Value = "x" },
                new SetupEnvironmentVariable { Name = "GH_TOKEN", Value = "someone-else" },
            ],
        }, "ghp_secret");

        Assert.Contains("export ANTHROPIC_API_KEY='sk-'\"'\"'x'\"'\"' y'\n", env);
        Assert.Contains($"export GIT_CONFIG_GLOBAL=\"$HOME/.deveagentmanager/users/{UserId:N}/gitconfig\"\n", env);
        Assert.Contains("export GH_TOKEN=ghp_secret\n", env);
        Assert.DoesNotContain("BAD", env);
        Assert.DoesNotContain("someone-else", env);
        Assert.Contains("unset GH_TOKEN\n", RunnerUsers.BuildEnvironment(UserId, "ketty", new UserProfile(), token: null));
    }

    [Fact]
    public void GitConfig_HasTheIdentity_AndOnlyTheUsersOwnCredentials()
    {
        if (!Which("git")) return;
        var folder = Path.Combine(_home, ".deveagentmanager", "users", UserId.ToString("N"));
        Directory.CreateDirectory(folder);
        // The shared settings, with the credentials helper and token from before users had their own: never used.
        File.WriteAllText(Path.Combine(_home, ".gitconfig"), "[init]\n\tdefaultBranch = main\n[credential]\n\thelper = store\n");
        File.WriteAllText(Path.Combine(_home, ".git-credentials"), "https://x-access-token:shared-token@github.com\n");
        File.WriteAllText(Path.Combine(folder, "git-credentials"), "https://x-access-token:ketty-token@github.com\n");
        File.WriteAllText(Path.Combine(folder, "gitconfig"),
            RunnerUsers.BuildGitConfig(UserId, "ketty", new UserProfile { GitUserName = "Ketty \"K\" Doe", GitUserEmail = "ketty@example.com" }, hasToken: true));

        Assert.Equal("Ketty \"K\" Doe", Git(folder, "config --get user.name").Output);
        Assert.Equal("ketty@example.com", Git(folder, "config --get user.email").Output);
        Assert.Equal("main", Git(folder, "config --get init.defaultBranch").Output);
        Assert.Contains("password=ketty-token", Git(folder, "credential fill", "protocol=https\nhost=github.com\n\n").Output);

        // Without a token of their own: no credentials at all, not the shared ones.
        File.WriteAllText(Path.Combine(folder, "gitconfig"), RunnerUsers.BuildGitConfig(UserId, "ketty", new UserProfile(), hasToken: false));
        var fill = Git(folder, "credential fill", "protocol=https\nhost=github.com\n\n");
        Assert.DoesNotContain("shared-token", fill.Output);
        Assert.NotEqual(0, fill.ExitCode);
        Assert.Equal("", Git(folder, "config --get user.name").Output);
    }

    [Fact]
    public void AuthorizedKeys_KeepTheManagersKey_AndReplaceOnlyTheManagedPart()
    {
        const string manager = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIManagerKeyManagerKeyManagerKey000 deveagentmanager";
        var first = RunnerUsers.MergeAuthorizedKeys(manager + "\n", [("ketty", [Key]), ("bob", [])]);
        Assert.StartsWith(manager + "\n", first);
        Assert.Contains("# ketty\n" + Key + "\n", first);
        Assert.DoesNotContain("# bob", first);

        // Again with the same keys: nothing changes. Without users: back to the Manager's key alone.
        Assert.Equal(first, RunnerUsers.MergeAuthorizedKeys(first, [("ketty", [Key])]));
        Assert.Equal(manager + "\n", RunnerUsers.MergeAuthorizedKeys(first, []));
        // A key that isn't one is left out rather than written.
        Assert.Equal(manager + "\n", RunnerUsers.MergeAuthorizedKeys(manager, [("ketty", ["rm -rf /"])]));
    }

    private (string Output, int ExitCode) Git(string folder, string arguments, string? input = null)
    {
        var start = new ProcessStartInfo("git", arguments.Split(' '))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = _home,
        };
        start.Environment["HOME"] = _home;
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(folder, "gitconfig");
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment.Remove("GIT_ASKPASS");
        start.Environment.Remove("SSH_ASKPASS");
        using var process = Process.Start(start)!;
        process.StandardInput.Write(input ?? "");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (output.TrimEnd('\n'), process.ExitCode);
    }

    private static bool Which(string tool)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("sh", ["-c", $"command -v {tool}"]) { RedirectStandardOutput = true })!;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => Directory.Delete(_home, true);
}
