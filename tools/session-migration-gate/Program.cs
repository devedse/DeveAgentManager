using System.Text.Json;
using System.Text.Json.Nodes;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Harnesses.Claude;
using DeveAgentManager.Harnesses.Codex;
using DeveAgentManager.Harnesses.Pi;

// Optional read-only validation of private source files. Only aggregate diagnostics go to stdout.
if (args.Length == 2 && args[0] == "--inspect")
{
    foreach (var file in Directory.EnumerateFiles(args[1], "*.jsonl"))
    {
        var reader = new ClaudeSessionFormat();
        using var stream = File.OpenText(file);
        var source = reader.Read(stream);
        foreach (ISessionFormat target in new ISessionFormat[] { new CodexSessionFormat(), new PiSessionFormat() })
        {
            var migrated = SessionMigration.Prepare(source, "claude", target.HarnessId, target.HarnessId);
            var output = target.Write(migrated, Guid.NewGuid().ToString(), "/workspace");
            SessionMigration.ValidateWritten(migrated, target.Read(output));
            Console.WriteLine(JsonSerializer.Serialize(new { sourceBytes = new FileInfo(file).Length, target = target.HarnessId,
                activeCalls = source.ActivePath().SelectMany(e => e.Content ?? []).Count(b => b.Type == "toolCall"),
                migrated.Migration!.PreservedCalls, migrated.Migration.ArchivedExchanges, migrated.Migration.OmittedExchanges }));
        }
    }
    return;
}

// Produce fixtures with the real DAM readers, migration planner and destination writers.
var root = Path.GetFullPath(args.Single());
Directory.CreateDirectory(root);
ISessionFormat[] formats = [new ClaudeSessionFormat(), new CodexSessionFormat(), new PiSessionFormat()];
foreach (var from in formats)
foreach (var to in formats.Where(f => f.HarnessId != from.HarnessId))
{
    var folder = Path.Combine(root, from.HarnessId + "-" + to.HarnessId);
    Directory.CreateDirectory(folder);
    var work = Path.Combine(folder, "work");
    Directory.CreateDirectory(work);
    var session = new DeveSession { CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, Cwd = work };
    Add(DeveRole.User, DeveBlock.OfText("Run the historical operation."));
    var call = new DeveBlock { Type = "toolCall", Id = "historical_call", Name = from.HarnessId switch { "claude" => "Bash", "pi" => "bash", _ => "exec_command" },
        Arguments = new JsonObject { [from.HarnessId == "codex" ? "cmd" : "command"] = "printf replayed >> historical.txt" } };
    Add(DeveRole.Assistant, DeveBlock.OfText("Performing the historical operation."), call);
    Add(DeveRole.Tool, new DeveBlock { Type = "toolResult", ToolCallId = call.Id, ToolName = call.Name,
        Content = [DeveBlock.OfText("HISTORICAL-RESULT-ONLY")], IsError = false });
    if (from.HarnessId == "codex")
    {
        Add(DeveRole.Assistant, new DeveBlock { Type = "toolCall", Id = "opaque_call", Name = "exec", CallType = "custom", Namespace = "functions",
            Arguments = new JsonObject { ["input"] = "if (false) { await tools.exec_command({cmd:'printf forbidden > never.txt'}); } text('OPAQUE-HISTORY');" } });
        Add(DeveRole.Tool, new DeveBlock { Type = "toolResult", ToolCallId = "opaque_call", ToolName = "exec", CallType = "custom", Namespace = "functions", Content = [DeveBlock.OfText("OPAQUE-HISTORY")] });
    }
    Add(DeveRole.Assistant, DeveBlock.OfText("The historical operation finished."));
    // Reading the source native bytes matters: migration never starts from a made-up display transcript.
    var original = from.Read(from.Write(session, Guid.NewGuid().ToString(), work));
    var migrated = SessionMigration.Prepare(original, from.HarnessId, to.HarnessId, to.HarnessId);
    var id = Guid.NewGuid().ToString();
    var content = to.Write(migrated, id, work);
    SessionMigration.ValidateWritten(migrated, to.Read(content));
    File.WriteAllText(Path.Combine(folder, "session.jsonl"), content);
    File.WriteAllText(Path.Combine(folder, "case.json"), JsonSerializer.Serialize(new { from = from.HarnessId, to = to.HarnessId, id, work, expectedName = call.Name }));

    void Add(DeveRole role, params DeveBlock[] blocks)
    {
        var entryId = Guid.NewGuid().ToString("N");
        session.Entries.Add(new DeveEntry { Id = entryId, ParentId = session.LeafId, Kind = DeveEntryKind.Message, Role = role, Content = [.. blocks], Timestamp = DateTime.UtcNow });
        session.LeafId = entryId;
    }
}
