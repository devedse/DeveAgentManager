using DeveAgentManager.Core.Setup;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>The Setup Profile's checks: shown next to the fields in Settings, and enforced when it is saved.</summary>
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
    public void VariableNames_AreWhatBashCanExport(string name, bool valid) => Assert.Equal(valid, SetupProfile.IsValidVariableName(name));

    [Fact]
    public void Variables_ValueWithoutNameOrTwiceTheSameName_AreRefused()
    {
        var blank = new SetupEnvironmentVariable();
        var nameless = new SetupEnvironmentVariable { Value = "sk-secret" };
        var first = new SetupEnvironmentVariable { Name = "KEY", Value = "1" };
        var second = new SetupEnvironmentVariable { Name = " KEY ", Value = "2" };
        var profile = new SetupProfile { EnvironmentVariables = [blank, nameless, first, second] };

        Assert.Null(profile.Problem(blank));
        Assert.NotNull(profile.Problem(nameless));
        Assert.Contains("twice", profile.Problem(first));
        Assert.Throws<ArgumentException>(profile.Validate);
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
