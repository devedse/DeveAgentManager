using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeveAgentManager.Core.Agents;

/// <summary>Owns bounded parsing diagnostics, separate from durable limit and continuation state.</summary>
public class UsageLimitReader(ILogger<UsageLimitReader> logger, TimeProvider clock)
{
    internal static readonly UsageLimitReader Default = new(NullLogger<UsageLimitReader>.Instance, TimeProvider.System);
    private sealed record Incident(string Fingerprint, DateTimeOffset LoggedAt, int Repeated);
    private readonly ConcurrentDictionary<(Guid, string), Incident> _incidents = new();
    public string? Problem(Guid agentId) => _incidents.Keys.Any(k => k.Item1 == agentId)
        ? "Usage-limit detection is temporarily unavailable. Saved settings are retained; retry detection." : null;

    public UsageLimitRead Screen(Agent agent, LimitScreen screen, DateTime now) => Read(agent, screen.Screen, screen.TimeZone,
        screen.ObservedAt ?? now, "screen");
    public UsageLimitRead Stored(Guid agentId, AgentLimit limit) => Read(
        new Agent { Id = agentId, HarnessId = limit.SessionId.Split('|')[0] }, limit.Message, limit.TimeZoneId, limit.DetectedAt, "stored-message");

    protected virtual ParsedUsageLimit? Parse(string harness, string text, string? zone, DateTime at, bool stored) =>
        stored ? UsageLimitParser.ParseStored(harness, text, zone, at) : UsageLimitParser.Parse(harness, text, zone, at);
    protected virtual string Sanitize(string text) => DiagnosticText.Redact(text);

    private UsageLimitRead Read(Agent agent, string text, string? zone, DateTime at, string source)
    {
        var read = UsageLimitRead.Capture(() => Parse(agent.HarnessId, text, zone, at, source == "stored-message"));
        var key = (agent.Id, source);
        if (read.Failure is not { } failure)
        {
            if (_incidents.TryRemove(key, out var recovered))
                logger.LogInformation("Usage-limit parsing recovered for {Agent} ({Source}); {Repeated} repeated failures suppressed", agent.Id, source, recovered.Repeated);
            return read;
        }
        // Bound retained state and fingerprint work, including oversized inputs rejected before matching.
        var subject = failure.Subject;
        var fingerprintText = subject.Length <= UsageLimitParser.MaxScreenLength ? subject : subject[..8192] + subject[^8192..];
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            failure.Stage + failure.Pattern + subject.Length + fingerprintText)));
        var now = clock.GetUtcNow();
        if (_incidents.TryGetValue(key, out var previous) && previous.Fingerprint == fingerprint && now - previous.LoggedAt < TimeSpan.FromMinutes(1))
        {
            _incidents.TryUpdate(key, previous with { Repeated = previous.Repeated + 1 }, previous);
            return read;
        }
        if (_incidents.Count >= 2048)
            foreach (var stale in _incidents.Keys.Take(32)) _incidents.TryRemove(stale, out _);
        _incidents[key] = new(fingerprint, now, 0);
        string input;
        try { input = DiagnosticText.Excerpt(Sanitize(subject), 4096); }
        catch (Exception) { input = "[Payload omitted: diagnostic sanitization failed]"; }
        logger.LogWarning("Usage-limit parsing unavailable for {Agent}; harness {Harness}, source {Source}, stage {Stage}, failure {Kind}, pattern {Pattern}, options {Options}, budget {BudgetMs}ms, input length {Length}, fingerprint {Fingerprint}, suppressed {Repeated}; sanitized input {Input}",
            agent.Id, agent.HarnessId, source, failure.Stage, failure.Kind, failure.Pattern ?? "none", failure.Options,
            failure.Timeout?.TotalMilliseconds, subject.Length, fingerprint, previous?.Repeated ?? 0, JsonSerializer.Serialize(input));
        return read;
    }
}
