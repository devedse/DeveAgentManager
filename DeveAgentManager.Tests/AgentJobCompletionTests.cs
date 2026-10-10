using System.Diagnostics;
using System.Text.Json;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class AgentJobCompletionTests
{
    [Fact]
    public async Task DurableHelperOwnsExitValidationReconnectAndOutbox()
    {
        if (OperatingSystem.IsWindows()) return;
        var start = new ProcessStartInfo("python3", [Path.Combine(AppContext.BaseDirectory, "Desktop", "jobs_test.py"),
            Path.Combine(AppContext.BaseDirectory, "Desktop", "dam-tools.py")]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, await output + await error);
    }

    [Fact]
    public void OnlyTheSelectedMatchingConversationMayResume()
    {
        var runner = Guid.NewGuid(); var agent = new Agent { Id = Guid.NewGuid(), RunnerId = runner, HarnessId = "codex", HarnessSessionId = "conversation" };
        var session = new StoredSession { AgentId = agent.Id, HarnessId = "codex", HarnessSessionId = "conversation" };
        Assert.True(AgentJobCompletions.Belongs(agent, session, runner));
        session.AgentId = null;
        Assert.False(AgentJobCompletions.Belongs(agent, session, runner));
        session.AgentId = agent.Id; session.HarnessSessionId = "another";
        Assert.False(AgentJobCompletions.Belongs(agent, session, runner));
        session.HarnessSessionId = "conversation";
        Assert.False(AgentJobCompletions.Belongs(agent, session, Guid.NewGuid()));
    }

    [Fact]
    public void CompletedAndFailedEvidenceRequireExactJobBinding()
    {
        var value = new { SchemaVersion = 1, EventId = "QueueCache-Campaign-test:terminal:1", CampaignId = "QueueCache-Campaign-test",
            ManifestSha256 = new string('a', 64), Directory = @"C:\Results\owned\QueueCache-Campaign-test", Status = "COMPLETED",
            ExpectedPhases = 2, CompletedPhases = 2, ExpectedCases = 3, CollectedCases = 3, Restoration = "RESTORED", Finished = DateTimeOffset.UtcNow };
        var job = new AgentJobPublication(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Test", "lab", @"C:\Results\owned", "VALIDATED", 0,
            DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(value), null);
        var runner = Guid.NewGuid();
        Assert.Equal(AgentJobCompletions.DeliveryKey(job, runner), AgentJobCompletions.DeliveryKey(job with { Id = Guid.NewGuid() }, runner));
        Assert.NotEqual(AgentJobCompletions.DeliveryKey(job, runner), AgentJobCompletions.DeliveryKey(job, Guid.NewGuid()));
        Assert.Contains("2/2 phases", AgentJobCompletions.Describe(job));
        Assert.Throws<InvalidDataException>(() => AgentJobCompletions.Describe(job with { OutputParent = @"C:\Results\another" }));
        Assert.Throws<InvalidDataException>(() => AgentJobCompletions.Describe(job with { Event = null }));
        Assert.Contains("requires inspection", AgentJobCompletions.Describe(job with { Event = null, Outcome = "INSPECTION_REQUIRED", ExitCode = 255 }));
    }
}
