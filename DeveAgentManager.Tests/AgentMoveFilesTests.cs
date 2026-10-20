using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Contracts;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class AgentMoveFilesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealSshMovePreservesWorkspaceNativeRecordsUploadsScreenshotsDocumentsAndModes(bool rollback)
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) return;
        Assert.SkipWhen(!File.Exists("/usr/sbin/sshd"), "Requires isolated OpenSSH.");
        await using var ssh = await RunnerShellIntegrationTests.Fixture.StartAsync();
        var home = Path.Combine(ssh.Root, "move-home");
        var workspace = home + "/work/project";
        var owner = Guid.NewGuid().ToString("N"); var agent = Guid.NewGuid();
        var runtime = home + $"/.deveagentmanager/agent-runtime/{owner}/{agent:N}/grok/sessions/session";
        var upload = home + "/.deveagentmanager/uploads/pasted.png";
        var document = home + "/.deveagentmanager/uploads/upload-id/report.pdf";
        var cached = home + "/.deveagentmanager/artifact-cache/fetched.png";
        Directory.CreateDirectory(workspace + "/empty"); Directory.CreateDirectory(runtime + "/compaction_checkpoints");
        Directory.CreateDirectory(Path.GetDirectoryName(document)!); Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        await File.WriteAllTextAsync(workspace + "/app.txt", "uncommitted project data", Ct);
        await File.WriteAllTextAsync(workspace + "/run.sh", "#!/bin/sh\necho test-only", Ct);
        File.SetUnixFileMode(workspace + "/run.sh", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.CreateSymbolicLink(workspace + "/project-link", "app.txt");
        await File.WriteAllBytesAsync(upload, [137, 80, 78, 71, 13, 10, 26, 10, 42], Ct);
        await File.WriteAllBytesAsync(document, Encoding.UTF8.GetBytes("%PDF-test-only"), Ct);
        await File.WriteAllTextAsync(cached, "fetched artifact", Ct);
        var native = JsonSerializer.Serialize(new { type = "user", image = upload, document, fetched = cached, nativeRecord = new string('x', 2_000_000) });
        await File.WriteAllTextAsync(runtime + "/acp.events.jsonl", native, Ct);
        await File.WriteAllTextAsync(runtime + "/summary.json", "{\"compacted\":true}", Ct);
        await File.WriteAllTextAsync(runtime + "/compaction_checkpoints/replay.json", "{\"replay\":\"exact\"}", Ct);
        var publication = home + "/.deveagentmanager/shared-files/" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(publication);
        await File.WriteAllTextAsync(publication + "/metadata.json", JsonSerializer.Serialize(new { agentId = agent, fileName = "shared.pdf" }), Ct);
        await File.WriteAllTextAsync(publication + "/file", "shared document", Ct);
        var move = new AgentMoveState(Guid.NewGuid().ToString("N"), agent, Guid.NewGuid(), Guid.NewGuid(), workspace, owner, agent.ToString("N"), AgentState.Stopped, false, Home: home,
            Histories: [new(runtime + "/acp.events.jsonl", "grok")]);
        var reports = new List<AgentMoveProgressDto>();
        async Task<string> ReportingPhase(string phase)
        {
            var result = new StringBuilder();
            var code = await ssh.Shell.RunStreamingAsync(ssh.Runner, AgentService.MoveCommand(phase, move with { ReportProgress = true }), line =>
            {
                if (line.StartsWith("DAM_MOVE_PROGRESS ", StringComparison.Ordinal)) reports.Add(JsonSerializer.Deserialize<AgentMoveProgressDto>(line[18..])!);
                else result.AppendLine(line);
            }, TimeSpan.FromMinutes(1), Ct);
            Assert.True(code == 0, result.ToString());
            return result.ToString().Trim();
        }
        var checksum = await ReportingPhase("pack");
        move = move with { ArchiveSha256 = checksum };
        var remote = home + "/.deveagentmanager/moves/" + move.Id + "/bundle.tar";
        var local = ssh.Root + "/transport.tar";
        var transferred = new List<(long Bytes, long Total, string Path)>();
        var indexPath = ssh.Root + "/transport.index";
        File.Copy(Path.GetDirectoryName(remote)! + "/transfer-index.jsonl", indexPath);
        using (var index = new MoveTransferIndex(indexPath))
        await using (var output = File.Create(local)) await ssh.Shell.DownloadToAsync(ssh.Runner, remote, output, Ct,
            (bytes, total) => transferred.Add((bytes, total, index.FileAt(bytes))));
        Assert.Contains(transferred, p => p.Path == runtime + "/acp.events.jsonl");
        Assert.Equal(new FileInfo(local).Length, transferred[^1].Bytes);
        Assert.All(transferred, p => Assert.Equal(new FileInfo(local).Length, p.Total));
        Assert.Equal(transferred.Select(p => p.Bytes).Order(), transferred.Select(p => p.Bytes));
        var indexEntries = (await File.ReadAllLinesAsync(indexPath, Ct)).Select(line => JsonDocument.Parse(line)).ToList();
        try
        {
            using var index = new MoveTransferIndex(indexPath);
            long previous = 0;
            foreach (var entry in indexEntries)
            {
                var end = entry.RootElement[0].GetInt64(); var path = entry.RootElement[1].GetString();
                Assert.True(end > previous); Assert.Equal(path, index.FileAt(previous + 1)); Assert.Equal(path, index.FileAt(end)); previous = end;
            }
        }
        finally { indexEntries.ForEach(e => e.Dispose()); }
        await using (var input = File.OpenRead(local)) Assert.Equal(move.ArchiveSha256, Convert.ToHexStringLower(await SHA256.HashDataAsync(input, Ct)));
        // Two isolated runner filesystems represented at the same absolute paths, as real VMs are.
        var source = home + "-source"; Directory.Move(home, source);
        Directory.CreateDirectory(Path.GetDirectoryName(remote)!);
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        await File.WriteAllTextAsync(cached, "fetched artifact", Ct); // A matching, pre-existing shared cache is reusable.
        var uploaded = new List<long>();
        await using (var input = File.OpenRead(local)) await ssh.Shell.UploadFromAsync(ssh.Runner, remote, input, Ct,
            (bytes, total) => { Assert.Equal(new FileInfo(local).Length, total); uploaded.Add(bytes); });
        Assert.Equal(new FileInfo(local).Length, uploaded[^1]);
        foreach (var phase in new[] { "unpack", "install" })
            await ReportingPhase(phase);
        Assert.Contains(reports, p => p.Stage == "Packing files" && p.TotalBytes > 0 && p.BytesCompleted == p.TotalBytes);
        Assert.Contains(reports, p => p.Stage == "Verifying restored files" && p.TotalBytes > 0 && p.BytesCompleted == p.TotalBytes);
        Assert.Contains(reports, p => p.Stage == "Installing and verifying destination files" && p.TotalBytes > 0 && p.BytesCompleted == p.TotalBytes);
        Assert.All(reports, p => { Assert.True(p.BytesCompleted >= 0); Assert.True(p.TotalBytes is null || p.BytesCompleted <= p.TotalBytes); });
        Assert.Equal("uncommitted project data", await File.ReadAllTextAsync(workspace + "/app.txt", Ct));
        Assert.True(Directory.Exists(workspace + "/empty")); Assert.Equal("app.txt", new FileInfo(workspace + "/project-link").LinkTarget);
        Assert.True(File.GetUnixFileMode(workspace + "/run.sh").HasFlag(UnixFileMode.UserExecute));
        Assert.Equal(native, await File.ReadAllTextAsync(runtime + "/acp.events.jsonl", Ct));
        Assert.Equal("{\"replay\":\"exact\"}", await File.ReadAllTextAsync(runtime + "/compaction_checkpoints/replay.json", Ct));
        Assert.Equal("%PDF-test-only", await File.ReadAllTextAsync(document, Ct));
        Assert.Equal("fetched artifact", await File.ReadAllTextAsync(cached, Ct));
        Assert.Equal("shared document", await File.ReadAllTextAsync(publication + "/file", Ct));
        Assert.Equal(9, new FileInfo(upload).Length);
        if (rollback)
        {
            // An interruption after installation but before commit must remove only move-owned files.
            (await ssh.Shell.RunAsync(ssh.Runner, AgentService.MoveCommand("cleanup-target", move), cancellationToken: Ct)).EnsureSuccess("rollback after install");
            Assert.False(Directory.Exists(workspace));
            Assert.False(File.Exists(upload));
            Assert.Equal("fetched artifact", await File.ReadAllTextAsync(cached, Ct));
            Assert.Equal(native, await File.ReadAllTextAsync(source + runtime[home.Length..] + "/acp.events.jsonl", Ct));
            Assert.Equal("uncommitted project data", await File.ReadAllTextAsync(source + "/work/project/app.txt", Ct));
            return;
        }
        // Commit removes the original workspace/runtime only after the new filesystem was verified.
        var target = home + "-target"; Directory.Move(home, target); Directory.Move(source, home);
        (await ssh.Shell.RunAsync(ssh.Runner, AgentService.MoveCommand("verify-source", move), cancellationToken: Ct)).EnsureSuccess("Source unchanged");
        (await ssh.Shell.RunAsync(ssh.Runner, AgentService.MoveCommand("cleanup-source", move), cancellationToken: Ct)).EnsureSuccess("Source cleanup");
        Assert.False(Directory.Exists(workspace)); Assert.False(Directory.Exists(Path.GetDirectoryName(runtime)!));
        Assert.True(File.Exists(target + "/work/project/app.txt"));
        Assert.True(File.Exists(target + document[home.Length..]));
    }

    [Theory(Timeout = 120_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CorruptTransferAndExistingDestinationRetainOriginalFiles(bool corrupt)
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/sshd"), "Requires isolated OpenSSH.");
        await using var ssh = await RunnerShellIntegrationTests.Fixture.StartAsync();
        var home = ssh.Root + "/move-home"; var workspace = home + "/work/project";
        Directory.CreateDirectory(workspace); await File.WriteAllTextAsync(workspace + "/valuable.txt", "original", Ct);
        var agent = Guid.NewGuid();
        var move = new AgentMoveState(Guid.NewGuid().ToString("N"), agent, Guid.NewGuid(), Guid.NewGuid(), workspace, Guid.NewGuid().ToString("N"), agent.ToString("N"), AgentState.Stopped, false, Home: home);
        var pack = (await ssh.Shell.RunAsync(ssh.Runner, AgentService.MoveCommand("pack", move), cancellationToken: Ct)).EnsureSuccess("pack");
        move = move with { ArchiveSha256 = pack.Output.Trim() };
        var archive = home + "/.deveagentmanager/moves/" + move.Id + "/bundle.tar";
        var local = await File.ReadAllBytesAsync(archive, Ct);
        var source = home + "-source"; Directory.Move(home, source);
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        if (corrupt) local[1024] ^= 0x40;
        else { Directory.CreateDirectory(workspace); await File.WriteAllTextAsync(workspace + "/existing.txt", "destination data", Ct); }
        await ssh.Shell.UploadAsync(ssh.Runner, archive, local, cancellationToken: Ct);
        var result = await ssh.Shell.RunAsync(ssh.Runner, AgentService.MoveCommand("unpack", move), cancellationToken: Ct);
        Assert.False(result.Succeeded);
        Assert.Contains(corrupt ? "checksum mismatch" : "already exists", result.Combined);
        (await ssh.Shell.RunAsync(ssh.Runner, AgentService.MoveCommand("cleanup-target", move), cancellationToken: Ct)).EnsureSuccess("rollback");
        Assert.Equal("original", await File.ReadAllTextAsync(source + "/work/project/valuable.txt", Ct));
        if (!corrupt) Assert.Equal("destination data", await File.ReadAllTextAsync(workspace + "/existing.txt", Ct));
        Assert.False(Directory.Exists(home + "/.deveagentmanager/moves/" + move.Id));
    }

    [Theory]
    [InlineData("/work/one", "/work/one/nested", true)]
    [InlineData("/work/one/../two", "/work/two", true)]
    [InlineData("/work/one", "/work/one-other", false)]
    public void SharedWorkspacesAreNotEligibleForDestructiveMoves(string a, string b, bool overlaps) =>
        Assert.Equal(overlaps, AgentService.OverlappingFolders(a, b));
}
