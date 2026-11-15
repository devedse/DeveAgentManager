using System.Diagnostics;
using DeveAgentManager.Core.Runners;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>What the GPU panel says, from what the Runner reports (<see cref="RunnerGpu.ReadScript"/>).</summary>
public sealed class RunnerGpuTests
{
    private const string Command = "pct set 30000 --dev0 /dev/dri/renderD128,gid=993";

    [Theory]
    [InlineData("gid=993\nmember=1", "not-attached", null)]
    [InlineData("gid=993\nmember=1\ndevice=/dev/dri/renderD128 65534 660", "no-access", null)]
    [InlineData("gid=993\ndevice=/dev/dri/renderD128 993 660", "drivers-missing", null)]
    [InlineData("gid=993\nmember=1\ndevice=/dev/dri/renderD128 993 660", "drivers-missing", null)]
    [InlineData("gid=993\nmember=1\ndevice=/dev/dri/renderD128 993 660\ndrivers=1\nvulkan=llvmpipe (LLVM 19.1.7, 256 bits)", "not-detected", null)]
    [InlineData("gid=993\nmember=1\ndevice=/dev/dri/renderD128 993 660\ndrivers=1\nvulkan=Intel(R) Graphics (BMG G21)\nvulkan=llvmpipe (LLVM 19.1.7, 256 bits)", "ready", "Intel(R) Graphics (BMG G21)")]
    [InlineData("gid=993\nmember=1\ndevice=/dev/dri/renderD128 0 666\ndrivers=1\nvulkan=AMD Radeon Graphics (RADV)", "ready", "AMD Radeon Graphics (RADV)")]
    public void ReportsWhatTheRunnerSees(string output, string state, string? device)
    {
        var gpu = RunnerGpu.Interpret(RunnerGpu.Parse(output), Command);
        Assert.Equal(state, gpu.State);
        Assert.Equal(device, gpu.DeviceName);
        Assert.Equal(Command, gpu.AttachCommand);
        Assert.False(string.IsNullOrWhiteSpace(gpu.Message));
    }

    [Fact]
    public void IgnoresLinesItDoesNotKnowAndBoundsDeviceNames()
    {
        var seen = RunnerGpu.Parse($"noise\ngid=abc\ndevice=/dev/dri/renderD128 x 660\nvulkan={new string('a', 500)}\n");
        Assert.Null(seen.RenderGroup);
        Assert.Empty(seen.Devices);
        Assert.Equal(120, Assert.Single(seen.VulkanDevices).Length);
    }

    [Fact]
    public async Task ScriptsAreValidBash()
    {
        foreach (var script in (string[])[RunnerGpu.ReadScript, RunnerGpu.InstallScript])
        {
            var start = new ProcessStartInfo("bash", ["-n", "-c", script]) { RedirectStandardError = true };
            using var process = Process.Start(start)!;
            var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, error);
        }
    }
}
