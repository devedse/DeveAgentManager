using System.Diagnostics;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class TouchInputTests
{
    [Fact]
    public async Task SchedulerUsesLocalTimezoneAndRejectsAmbiguousClockChanges()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Js");
        using var process = Process.Start(new ProcessStartInfo("node", [Path.Combine(folder, "schedule-time.test.cjs"), Path.Combine(folder, "schedule-time.js")])
            { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, await output + await error);
    }

    [Fact]
    public async Task AutocorrectAndBackspacePreserveTheDraftAndComposition()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Js");
        var start = new ProcessStartInfo("node", [Path.Combine(folder, "terminal-touch-input.test.cjs"), Path.Combine(folder, "terminal-touch-input.js")])
        { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, await output + await error);
    }
}
