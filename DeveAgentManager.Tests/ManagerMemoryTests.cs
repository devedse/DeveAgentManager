// Short in-memory requests: cancelling them with the test run adds nothing.
#pragma warning disable xUnit1051

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// What keeps the Manager inside its memory (docs/manager-memory.md): the conversation page streams a conversation and
/// refuses one too large to load, and a Manager killed for running out of memory is reported at the next start with what
/// it was doing.
/// </summary>
public sealed class ManagerMemoryTests
{
    private const string Password = "memory-test-password-1";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConversationContentIsStreamed_AndTooLargeOnesAreRefusedWithAReadableReason()
    {
        await using var manager = new Manager();
        var client = manager.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAccountAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["username"] = "admin", ["password"] = Password, ["returnUrl"] = "/" }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var small = Guid.NewGuid();
        var large = Guid.NewGuid();
        await using (var db = await manager.Services.GetRequiredService<IDbContextFactory<ManagerDbContext>>().CreateDbContextAsync(Ct))
        {
            db.Sessions.Add(new StoredSession { Id = small, OwnerId = User.BootstrapId, Title = "small", HarnessId = "claude" });
            db.Sessions.Add(new StoredSession { Id = large, OwnerId = User.BootstrapId, Title = "large", HarnessId = "claude" });
            await db.SaveChangesAsync(Ct);
        }

        // Saved conversations: the size that counts is what is stored, as the Sessions page shows it.
        var sessions = manager.Services.GetRequiredService<ManagerOptions>().SessionsDirectory;
        Directory.CreateDirectory(sessions);
        DeveSession Conversation(int characters) => new() { Entries = [new() { Kind = DeveEntryKind.Message, Role = DeveRole.Assistant, Text = new string('x', characters) }] };
        await File.WriteAllTextAsync(Path.Combine(sessions, $"{small:N}.json"), Conversation(100).ToJson(), Ct);
        await File.WriteAllTextAsync(Path.Combine(sessions, $"{large:N}.json"), Conversation(3 * 1024 * 1024 + 4096).ToJson(), Ct);

        var content = await client.GetAsync($"/api/sessions/{small}/content");
        content.EnsureSuccessStatusCode();
        Assert.Equal("application/json", content.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new string('x', 100), (await DeveSession.ReadAsync(await content.Content.ReadAsStreamAsync())).Entries.Single().Text);

        // 3 MB against a 2 MB limit: refused before anything is loaded, with a reason the page shows.
        var refused = await client.GetAsync($"/api/sessions/{large}/content");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("This conversation is 3 MB, more than the 2 MB the Manager shows here.", problem.GetProperty("detail").GetString());
        // The facts about it stay available.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/sessions/{large}")).StatusCode);
    }

    [Fact]
    public async Task AProcessKilledWithoutShuttingDownIsReportedAtTheNextStart_WithItsLastReadingAndRequests()
    {
        var data = Directory.CreateTempSubdirectory("dam-memory-").FullName;
        try
        {
            var options = new ManagerOptions { DataDirectory = data, AdminPassword = "test-only-password" };
            var heartbeat = Path.Combine(data, "diagnostics", "heartbeat.json");
            Directory.CreateDirectory(Path.GetDirectoryName(heartbeat)!);
            const long mb = 1048576;
            await File.WriteAllTextAsync(heartbeat, JsonSerializer.Serialize(new MemoryWatch.Heartbeat(new DateTime(2026, 10, 21, 13, 1, 39, DateTimeKind.Utc),
                new DateTime(2026, 10, 21, 13, 2, 29, DateTimeKind.Utc), 3100 * mb, 2900 * mb, 3000 * mb, 140 * mb, 3850 * mb,
                ["GET /api/sessions/00000000-0000-0000-0000-0000000000aa/content for 41 s"])), Ct);

            var logger = await RunAsync(options, stopCleanly: true);
            var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
            Assert.Contains("ended without shutting down, after 2026-10-21 13:02:29Z", warning.Message);
            Assert.Contains("working set 3100 MB, .NET heap 2900 of 3000 MB, machine 140 of 3850 MB available", warning.Message);
            Assert.Contains("most likely killed it for running out of memory", warning.Message);
            Assert.Contains("GET /api/sessions/00000000-0000-0000-0000-0000000000aa/content for 41 s", warning.Message);

            // That process shut down cleanly: nothing to report at the next start.
            Assert.True(JsonSerializer.Deserialize<MemoryWatch.Heartbeat>(await File.ReadAllTextAsync(heartbeat, Ct))!.CleanShutdown);
            Assert.DoesNotContain((await RunAsync(options, stopCleanly: true)).Entries, e => e.Level == LogLevel.Warning);
        }
        finally { Directory.Delete(data, true); }
    }

    [Fact]
    public async Task RequestsInProgressAreListedLongestFirst_WithoutTheirQueryOrSockets()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/test-only/long-request";
        context.Request.QueryString = new QueryString("?token=test-only-secret");
        IReadOnlyList<string> during = [];
        await MemoryWatch.TrackRequestsAsync(context, () =>
        {
            during = MemoryWatch.RunningRequests(DateTime.UtcNow, take: 1000);
            return Task.CompletedTask;
        });
        var listed = Assert.Single(during, r => r.Contains("/api/test-only/long-request"));
        Assert.StartsWith("GET /api/test-only/long-request for ", listed);
        Assert.DoesNotContain("token", listed);
        Assert.DoesNotContain(MemoryWatch.RunningRequests(DateTime.UtcNow, take: 1000), r => r.Contains("/api/test-only/long-request"));

        var hub = new DefaultHttpContext();
        hub.Request.Path = "/hubs/live";
        await MemoryWatch.TrackRequestsAsync(hub, () =>
        {
            Assert.DoesNotContain(MemoryWatch.RunningRequests(DateTime.UtcNow, take: 1000), r => r.Contains("/hubs/live"));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void LowMemoryMeansTheMachineOrTheDotNetHeapIsNearlyFull()
    {
        MemoryWatch.Heartbeat Reading(long heap, long limit, long? available, long? total) => new(DateTime.UtcNow, DateTime.UtcNow, 1, heap, limit, available, total, []);
        Assert.False(MemoryWatch.IsLow(Reading(100, 1000, 500, 1000)));
        Assert.True(MemoryWatch.IsLow(Reading(100, 1000, 140, 1000)));
        Assert.True(MemoryWatch.IsLow(Reading(810, 1000, 500, 1000)));
        Assert.False(MemoryWatch.IsLow(Reading(100, 0, null, null)));

        var meminfo = Path.GetTempFileName();
        try
        {
            File.WriteAllText(meminfo, "MemTotal:       3940000 kB\nMemFree:          104448 kB\nMemAvailable:     144384 kB\n");
            Assert.Equal((144384L * 1024, 3940000L * 1024), MemoryWatch.SystemMemory(meminfo));
        }
        finally { File.Delete(meminfo); }
        Assert.Equal((null, null), MemoryWatch.SystemMemory("/nonexistent/test-only-meminfo"));
    }

    // Starts the watch as at start-up, lets it write a heartbeat, then shuts it down.
    private static async Task<CapturedLogger<MemoryWatch>> RunAsync(ManagerOptions options, bool stopCleanly)
    {
        var logger = new CapturedLogger<MemoryWatch>();
        var lifetime = new Lifetime();
        using var watch = new MemoryWatch(options, lifetime, logger);
        await watch.StartAsync(Ct);
        await Task.Delay(300, Ct);
        if (stopCleanly) lifetime.Stop();
        await watch.StopAsync(Ct);
        return logger;
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stop();
        public void Stop() => _stopping.Cancel();
    }

    private sealed class Manager : WebApplicationFactory<DeveAgentManager.Api.DtoMapper>
    {
        private readonly string _data = Directory.CreateTempSubdirectory("dam-memory-api-").FullName;

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("DATA_DIR", _data);
            builder.UseSetting("ADMIN_USERNAME", "admin");
            builder.UseSetting("ADMIN_PASSWORD", Password);
            builder.UseSetting("PROXMOX_URL", "");
            builder.UseSetting("CONVERSATION_VIEW_MAX_MB", "2");
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            try { Directory.Delete(_data, true); }
            catch (IOException) { } // Still in use by a background service; it's a temp folder.
        }
    }
}
