using Corsinvest.ProxmoxVE.Api.Shared.Models.Vm;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Setup;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Providers.Proxmox;
using System.Text;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>The parsing and quoting that everything on a Runner depends on.</summary>
public class CoreTests
{
    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("with space", "'with space'")]
    [InlineData("it's", "'it'\"'\"'s'")]
    [InlineData("", "''")]
    [InlineData("$HOME;rm -rf /", "'$HOME;rm -rf /'")]
    public void Quote_MakesOneLiteralShellWord(string value, string expected) => Assert.Equal(expected, ShellQuote.Quote(value));

    [Fact]
    public void Path_ExpandsHomeEvenWhenQuoted()
    {
        Assert.Equal("\"$HOME\"", ShellQuote.Path("~"));
        Assert.Equal("\"$HOME\"/'my dir/x'", ShellQuote.Path("~/my dir/x"));
        Assert.Equal("/etc/x", ShellQuote.Path("/etc/x"));
    }

    [Fact]
    public void Wrap_NormalizesWindowsLineEndings()
    {
        var wrapped = RunnerShell.Wrap("set -e\r\necho hi\r\n");
        Assert.DoesNotContain('\r', wrapped);
        Assert.StartsWith("bash -c ", wrapped);
    }

    [Fact]
    public void ParsePanes_ReadsTmuxState()
    {
        var (now, panes) = AgentService.ParsePanes("1790545300\ndam-a|0||1790545299\ndam-b|1|3|1790545000\ndam-c|0|\ngarbage\n");
        Assert.Equal(1790545300, now);
        Assert.Equal((false, (int?)null, (long?)1790545299), panes["dam-a"]);
        Assert.Equal((true, (int?)3, (long?)1790545000), panes["dam-b"]);
        Assert.Null(panes["dam-c"].LastOutput);
        Assert.Equal(3, panes.Count);
    }

    [Theory]
    [InlineData("github.com/devedse/DeveMazeGeneratorCore", "https://github.com/devedse/DeveMazeGeneratorCore")]
    [InlineData("https://github.com/a/b.git", "https://github.com/a/b.git")]
    [InlineData("git@github.com:a/b.git", "git@github.com:a/b.git")]
    public void NormalizeRepositoryUrl_AcceptsShortForms(string input, string expected) => Assert.Equal(expected, AgentService.NormalizeRepositoryUrl(input));

    [Fact]
    public void BuildAgentEnv_QuotesValuesAndSkipsInvalidNames()
    {
        var env = RunnerSetupService.BuildAgentEnv(new SetupProfile
        {
            EnvironmentVariables =
            [
                new SetupEnvironmentVariable { Name = "ANTHROPIC_API_KEY", Value = "sk-'x' y" },
                new SetupEnvironmentVariable { Name = "BAD NAME", Value = "x" },
                new SetupEnvironmentVariable { Name = "1BAD", Value = "x" },
            ],
        });

        Assert.Contains("export ANTHROPIC_API_KEY='sk-'\"'\"'x'\"'\"' y'\n", env);
        Assert.DoesNotContain("BAD", env.Replace("ANTHROPIC", ""));
    }

    [Fact]
    public void StartupOutputFilter_HidesEverythingBeforeTheMarker()
    {
        var filter = new StartupOutputFilter();
        // The echoed command contains the escaped marker, which must not count.
        Assert.Empty(filter.Process(Encoding.UTF8.GetBytes($"motd\r\n$ printf '{StartupOutputFilter.EscapedMarker}'\r\n")));
        var rest = filter.Process(Encoding.UTF8.GetBytes($"{StartupOutputFilter.Marker}\r\nhello"));
        Assert.Equal("hello", Encoding.UTF8.GetString(rest));
        Assert.Equal("more", Encoding.UTF8.GetString(filter.Process("more"u8)));
    }

    [Theory]
    [InlineData("31000-31099", 31000, 31099)]
    [InlineData("nonsense", 30000, 30999)]
    [InlineData("200-100", 30000, 30999)]
    public void ParseRange_FallsBackOnBadInput(string value, int start, int end) => Assert.Equal((start, end), ProxmoxOptions.ParseRange(value));

    [Fact]
    public void ReplaceBridge_KeepsTheRestOfTheNic() =>
        Assert.Equal("virtio=BC:24:11:00:00:01,bridge=vmbr1,firewall=1", ProxmoxProvider.ReplaceBridge("virtio=BC:24:11:00:00:01,bridge=vmbr0,firewall=1", "vmbr1"));

    [Fact]
    public void SelectAddress_PicksTheFirstRealIpv4()
    {
        var interfaces = new VmQemuAgentNetworkGetInterfaces
        {
            Result =
            [
                Nic("lo", "127.0.0.1"),
                Nic("docker0", "172.17.0.1"),
                Nic("eth0", "fe80::1", "ipv6"),
                Nic("eth0", "192.168.1.50"),
            ],
        };

        Assert.Equal("192.168.1.50", ProxmoxProvider.SelectAddress(interfaces));
        Assert.Null(ProxmoxProvider.SelectAddress(new VmQemuAgentNetworkGetInterfaces { Result = [Nic("lo", "127.0.0.1")] }));
    }

    private static VmQemuAgentNetworkGetInterfaces.ResultInfo Nic(string name, string ip, string type = "ipv4") => new()
    {
        Name = name,
        IpAddresses = [new VmQemuAgentNetworkGetInterfaces.Ip { IpAddress = ip, IpAddressType = type }],
    };
}
