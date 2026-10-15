using System.Text.RegularExpressions;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;

internal sealed class FaultingLimitReader(ILogger<UsageLimitReader>? logger = null)
    : UsageLimitReader(logger ?? NullLogger<UsageLimitReader>.Instance, TimeProvider.System)
{
    public bool FailScreen, FailStored, FailSanitizer;
    protected override ParsedUsageLimit? Parse(string harness, string text, string? zone, DateTime at, bool stored)
    {
        if (stored ? FailStored : FailScreen)
            throw new RegexMatchTimeoutException(text, @"^\s*[›❯](?:\s|$)", TimeSpan.FromMilliseconds(100));
        return base.Parse(harness, text, zone, at, stored);
    }
    protected override string Sanitize(string text) => FailSanitizer ? throw new InvalidOperationException("test-only sanitizer failure") : base.Sanitize(text);
}

internal sealed class CapturedLogger<T> : ILogger<T>
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();
    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries => _entries.ToArray();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue((level, formatter(state, exception), exception));
}

public class UsageLimitFailureTests
{
    [Fact]
    public void UnsupportedClockDigitsKeepLimitRecognizedWithoutThrowing()
    {
        var parsed = UsageLimitParser.Inspect("codex", UsageLimitParserTests.Codex("١:٠٠ PM"), "UTC", DateTime.UtcNow);
        Assert.True(parsed.Available);
        Assert.NotNull(parsed.Limit);
        Assert.Null(parsed.Limit.ResetAt);
    }

    [Fact]
    public void TimeoutRecordsExpressionAndEscapedSanitizedSubjectBeforeAnySink()
    {
        var log = new CapturedLogger<UsageLimitReader>();
        var reader = new FaultingLimitReader(log) { FailScreen = true };
        var agent = new Agent { Id = Guid.NewGuid(), HarnessId = "codex" };
        const string text = "  input\t›\npassword=test-only-sensitive Bearer test-only-token\n";
        var screen = new LimitScreen(text, "identity", "UTC", true, "hash");
        var result = reader.Screen(agent, screen, DateTime.UtcNow);
        Assert.False(result.Available);
        Assert.Null(result.Limit);
        var message = Assert.Single(log.Entries).Message;
        Assert.Contains(@"^\s*[›❯](?:\s|$)", message);
        Assert.Contains("input length " + text.Length, message);
        Assert.Contains(@"input\t", message);
        Assert.Contains(@"\n", message);
        Assert.Contains("[redacted]", message);
        Assert.DoesNotContain("test-only-sensitive", message);
        Assert.DoesNotContain("test-only-token", message);
        reader.Screen(agent, screen, DateTime.UtcNow);
        Assert.Single(log.Entries);
        reader.FailScreen = false;
        Assert.True(reader.Screen(agent, screen, DateTime.UtcNow).Available);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("recovered"));
    }

    [Fact]
    public void SanitizerFailureRetainsMetadataWithoutRawPayload()
    {
        var log = new CapturedLogger<UsageLimitReader>();
        var reader = new FaultingLimitReader(log) { FailScreen = true, FailSanitizer = true };
        var result = reader.Screen(new Agent { Id = Guid.NewGuid(), HarnessId = "claude" },
            new("test-only-private-text", "identity", "UTC", true, "hash"), DateTime.UtcNow);
        Assert.False(result.Available);
        Assert.Contains("input length 22", Assert.Single(log.Entries).Message);
        Assert.Contains("sanitization failed", log.Entries[0].Message);
        Assert.DoesNotContain("test-only-private-text", log.Entries[0].Message);
    }

    [Fact]
    public void OversizedRelevantInputIsUnavailableRatherThanNoLimit_AndDiagnosticsAreBounded()
    {
        var log = new CapturedLogger<UsageLimitReader>();
        var reader = new UsageLimitReader(log, TimeProvider.System);
        var agent = new Agent { Id = Guid.NewGuid(), HarnessId = "codex" };
        var result = reader.Screen(agent, new(new string(' ', UsageLimitParser.MaxLineLength + 1) + "\n› ", "identity", "UTC", true, "hash"), DateTime.UtcNow);
        Assert.False(result.Available);
        Assert.Equal("line-length", result.Failure?.Stage);
        Assert.InRange(Assert.Single(log.Entries).Message.Length, 1, 5500);
        Assert.Contains("truncated", log.Entries[0].Message);
        Assert.True(UsageLimitParser.Inspect("codex", "ordinary output\n› ", "UTC", DateTime.UtcNow).Available);
        Assert.Null(UsageLimitParser.Inspect("codex", "ordinary output\n› ", "UTC", DateTime.UtcNow).Limit);
    }

    [Theory]
    [InlineData("password=\"test-only-secret without closing quote")]
    [InlineData("api_key='test-only-secret with spaces'")]
    [InlineData("Authorization: Bearer test-only-secret")]
    [InlineData("Authorization: Basic test-only-secret")]
    [InlineData("Cookie: session=test-only-secret; another=test-only-secret")]
    [InlineData("-----BEGIN PRIVATE KEY-----\ntest-only-secret\n-----END PRIVATE KEY-----")]
    public void DiagnosticRedactionHandlesQuotedTokensAndKeys(string text)
    {
        Assert.DoesNotContain("test-only-secret", DiagnosticText.Redact(text));
    }
}
