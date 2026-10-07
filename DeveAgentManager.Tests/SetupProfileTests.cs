using DeveAgentManager.Core.Setup;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// The checks of the shared Setup Profile and of each user's own settings: shown next to the fields in Settings, and
/// enforced when they are saved.
/// </summary>
public class SetupProfileTests
{
    [Theory]
    [InlineData("ANTHROPIC_API_KEY", true)]
    [InlineData("_private", true)]
    [InlineData("a1", true)]
    [InlineData("1ABC", false)]
    [InlineData("MY-KEY", false)]
    [InlineData("MY KEY", false)]
    [InlineData("KEY=1", false)]
    [InlineData("", false)]
    public void VariableNames_AreWhatBashCanExport(string name, bool valid) => Assert.Equal(valid, UserProfile.IsValidVariableName(name));

    [Fact]
    public void Variables_ValueWithoutNameOrTwiceTheSameName_AreRefused()
    {
        var blank = new SetupEnvironmentVariable();
        var nameless = new SetupEnvironmentVariable { Value = "sk-secret" };
        var first = new SetupEnvironmentVariable { Name = "KEY", Value = "1" };
        var second = new SetupEnvironmentVariable { Name = " KEY ", Value = "2" };
        var profile = new UserProfile { EnvironmentVariables = [blank, nameless, first, second] };

        Assert.Null(profile.Problem(blank));
        Assert.NotNull(profile.Problem(nameless));
        Assert.Contains("twice", profile.Problem(first));
        Assert.Throws<ArgumentException>(profile.Validate);
    }

    [Theory]
    [InlineData("GH_TOKEN")]
    [InlineData("GIT_CONFIG_GLOBAL")]
    [InlineData("DAM_SESSION_ID")]
    public void Variables_TheManagerSets_AreRefused(string name)
    {
        var profile = new UserProfile { EnvironmentVariables = [new SetupEnvironmentVariable { Name = name, Value = "x" }] };
        Assert.Contains("set by the Manager", profile.Problem(profile.EnvironmentVariables[0]));
        Assert.Throws<ArgumentException>(profile.Validate);
    }

    [Theory]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGcWbW5Um3OldSwXFQ2PyjmHd9lU0w3tqxRrW+yN1kFH ketty@laptop", true)]
    [InlineData("ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTY=", true)]
    [InlineData("", true)]
    [InlineData("AAAAC3NzaC1lZDI1NTE5AAAAIGcWbW5Um3Old", false)]
    [InlineData("ssh-ed25519 not-base64!", false)]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGcWbW5Um3Old\nssh-rsa AAAAB3NzaC1yc2E", false)]
    public void SshKeys_ArePublicKeysOnOneLine(string key, bool valid) => Assert.Equal(valid, UserProfile.SshKeyProblem(key) is null);

    [Fact]
    public void Cleaned_DropsEmptyRowsAndDuplicateKeys_AndKeepsTheIdentityOnOneLine()
    {
        const string key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGcWbW5Um3OldSwXFQ2PyjmHd9lU0w3tqxRrW+yN1kFH";
        var clean = new UserProfile
        {
            GitUserName = "  Ketty \n Doe ",
            EnvironmentVariables = [new SetupEnvironmentVariable(), new SetupEnvironmentVariable { Name = " KEY ", Value = " v " }],
            SshKeys = ["", key, " " + key + " "],
        }.Cleaned();

        Assert.Equal("Ketty Doe", clean.GitUserName);
        Assert.Null(clean.GitUserEmail);
        Assert.Equal(("KEY", " v "), (Assert.Single(clean.EnvironmentVariables).Name, clean.EnvironmentVariables[0].Value));
        Assert.Equal([key], clean.SshKeys);
    }

    [Theory]
    [InlineData("~/.pi/agent/AGENTS.md", "600", true)]
    [InlineData("/etc/motd", "0644", true)]
    [InlineData(".pi/agent/AGENTS.md", "600", false)]
    [InlineData("~/x", "800", false)]
    [InlineData("~/x", "rw", false)]
    [InlineData("~/x", "", false)]
    public void Files_NeedAFullPathAndAnOctalMode(string path, string mode, bool valid)
    {
        var profile = new SetupProfile { Files = [new SetupFile { Path = path, Content = "x", Mode = mode }] };
        if (valid)
        {
            profile.Validate();
        }
        else
        {
            Assert.Throws<ArgumentException>(profile.Validate);
        }
    }

    [Fact]
    public void Files_ContentWithoutPath_IsRefused_ButAnEmptyRowIsFine()
    {
        new SetupProfile { Files = [new SetupFile()] }.Validate();
        Assert.Throws<ArgumentException>(new SetupProfile { Files = [new SetupFile { Content = "token" }] }.Validate);
    }

    [Fact]
    public void Packages_WithSpaces_AreRefused()
    {
        Assert.Null(HarnessConfig.PackageProblem("npm:pi-web-access"));
        Assert.NotNull(HarnessConfig.PackageProblem("npm:pi web"));
        var profile = new SetupProfile();
        profile.GetHarness("pi").Packages.Add("pi install npm:x");
        Assert.Throws<ArgumentException>(profile.Validate);
    }
}
