using DeveAgentManager.Client.Shared;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class VoiceTranscriptLogTests
{
    [Fact]
    public void DuplexCaptionsShowOrdinarySpeechAndReplacePartialsWithFinalText()
    {
        var log = new VoiceTranscriptLog();
        log.Update(new("user-one", "user", "Hello,", false, false));
        log.Update(new("assistant-one", "assistant", "Hi", false, false));
        log.Update(new("user-one", null, " how are you?", true, false));
        log.Update(new("assistant-one", null, " there!", true, false));
        Assert.Equal(new[] { "Hello, how are you?", "Hi there!" }, log.Lines.Select(line => line.Text));
        Assert.False(log.Update(new("user-one", "user", "Hello,", false, false)));
        log.Update(new("user-one", "user", "Hello, how are you doing?", false, true));
        log.Update(new("assistant-one", "assistant", "Hi there!", false, true));
        Assert.False(log.Update(new("user-one", "user", "Hello, how are you doing?", false, true)));
        Assert.False(log.Update(new("user-one", null, " repeated late chunk", true, false)));
        Assert.Equal(new[] { "Hello, how are you doing?", "Hi there!" }, log.Lines.Select(line => line.Text));
        Assert.Equal(new[] { "user", "assistant" }, log.Lines.Select(line => line.Role));
        Assert.All(log.Lines, line => Assert.True(line.Complete));
    }

    [Fact]
    public void EndingCallKeepsPartialWordsAndAnotherCallCanReuseAProviderId()
    {
        var log = new VoiceTranscriptLog();
        log.Update(new("1:turn", "user", "Keep this unfinished sentence", false, false));
        log.Finish();
        Assert.False(log.Update(new("1:turn", null, " late data", true, false)));
        log.Update(new("3:turn", "user", "Next call", false, false));
        log.Update(new("3:turn", "user", "", false, true));
        Assert.Equal(new[] { "Keep this unfinished sentence", "Next call" }, log.Lines.Select(line => line.Text));
        Assert.All(log.Lines, line => Assert.True(line.Complete));
    }

    [Fact]
    public void UntrustedOrUnboundedEventsCannotGrowCaptionsOrChangeTheirSpeaker()
    {
        var log = new VoiceTranscriptLog();
        Assert.False(log.Update(null));
        Assert.False(log.Update(new("unknown", null, "unattributed delta", true, false)));
        Assert.False(log.Update(new("system", "system", "hidden context", false, false)));
        Assert.False(log.Update(new("oversized", "user", new string('x', 100000), false, false)));
        log.Update(new("speaker", "user", "Actual speech", false, false));
        Assert.False(log.Update(new("speaker", "assistant", "Wrong speaker", false, true)));
        for (var i = 0; i < 600; i++) log.Update(new($"turn-{i}", "user", "Speech " + i, false, true));
        Assert.True(log.Lines.Count <= 500);
        Assert.Equal("Speech 599", log.Lines[^1].Text);
    }
}
