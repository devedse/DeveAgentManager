using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Agents;

/// <summary>Bounded semantic history only. Never records full terminal screens or typed prompts.</summary>
public sealed class AgentDiagnostics(ManagerOptions options, TimeProvider clock, ILogger<AgentDiagnostics> logger)
{
    private readonly object _gate = new();
    private string PathFor(Guid id) => Path.Combine(options.LogsDirectory, "agents", id + ".json");
    public IReadOnlyList<AgentDiagnosticDto> Read(Guid id)
    {
        lock (_gate) return Load(id).Where(x => x.At >= clock.GetUtcNow().UtcDateTime.AddDays(-7)).TakeLast(100).ToArray();
    }
    private List<AgentDiagnosticDto> Load(Guid id)
    {
        try { return File.Exists(PathFor(id)) ? JsonSerializer.Deserialize<List<AgentDiagnosticDto>>(File.ReadAllText(PathFor(id))) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { logger.LogWarning(ex, "Could not read agent diagnostics for {Agent}", id); return []; }
    }
    public void Record(Guid id, string category, string message, string? rule = null)
    {
        lock (_gate)
        {
            try
            {
                var rows = Read(id).ToList();
                message = message.Length > 500 ? message[..500] : message;
                if (rows.LastOrDefault() is { } last && last.Category == category && last.Message == message && last.Rule == rule) return;
                rows.Add(new(clock.GetUtcNow().UtcDateTime, category, message, rule));
                var path = PathFor(id);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(rows.TakeLast(100)));
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { logger.LogWarning(ex, "Could not save agent diagnostics for {Agent}", id); }
        }
    }
}
