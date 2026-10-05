using System.Diagnostics;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Setup;
using Xunit;

namespace DeveAgentManager.Tests;

public class RunnerDesktopTests
{
    [Fact]
    public void DesktopSetupAndConnectionRegressions()
    {
        if (OperatingSystem.IsWindows()) return;
        var script = Path.Combine(AppContext.BaseDirectory, "Desktop", "desktop.test.py");
        var start = new ProcessStartInfo("python3", [script]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "Desktop helper tests timed out.");
        Assert.True(process.ExitCode == 0, output + error);
    }

    [Fact]
    public void OnlyDesktopRunnersLoadTheGraphicalEnvironment()
    {
        Assert.DoesNotContain("desktop-env", RunnerSetupService.BuildAgentEnv(new SetupProfile()));
        Assert.Contains("desktop-env", RunnerSetupService.BuildAgentEnv(new SetupProfile(), desktop: true));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"state\":\"ready\"}")]
    [InlineData("{\"state\":\"unexpected\",\"message\":\"hello\"}")]
    public void InvalidStatusNeverClaimsDesktopIsReady(string text) => Assert.Equal("unavailable", RunnerDesktop.Parse(text).State);

    [Fact]
    public void DesktopStatusIncludesBackendAndUiAvailability()
    {
        var status = RunnerDesktop.Parse("{\"state\":\"ready\",\"message\":\"Desktop ready\",\"sessionType\":\"x11\",\"display\":\":4\",\"automationAvailable\":true}");
        Assert.Equal("ready", status.State);
        Assert.Equal(":4", status.Display);
        Assert.True(status.AutomationAvailable);
    }

    [Theory]
    [InlineData("runner-setup.sh")]
    [InlineData("desktop-setup.sh")]
    public void SetupScriptsHaveValidBashSyntax(string name)
    {
        if (OperatingSystem.IsWindows()) return;
        using var process = Process.Start(new ProcessStartInfo("bash", ["-n"]) { RedirectStandardInput = true, RedirectStandardError = true })!;
        process.StandardInput.Write(RunnerSetupService.ReadResource(name));
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }
}
