using System.Net;
using System.Net.Http.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Runners;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DeveAgentManager.Tests;

public class UsageLimitApiFailureTests
{
    [Fact]
    public async Task LimitApiReturnsSavedScheduleDuringParsingFailure_AndRecovers()
    {
        await using var manager = new Manager();
        var ct = TestContext.Current.CancellationToken;
        using var client = manager.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var login = await client.PostAccountAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["username"] = "admin", ["password"] = "parser-test-only-password", ["returnUrl"] = "/" }), ct);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var db = await manager.Services.GetRequiredService<IDbContextFactory<ManagerDbContext>>().CreateDbContextAsync(ct))
        {
            var runner = new Runner { Id = Guid.NewGuid(), OwnerId = User.BootstrapId, Name = "test-runner", State = RunnerState.Stopped };
            var agent = new Agent { Id = id, OwnerId = User.BootstrapId, RunnerId = runner.Id, Name = "test-agent", HarnessId = "codex", HarnessSessionId = "test-session" };
            db.Runners.Add(runner);
            db.Agents.Add(agent);
            db.AgentLimits.Add(new AgentLimit { AgentId = id, SessionId = UsageLimitState.Session(agent), Message = "■ You've hit your usage limit. Try again at tomorrow 3:00 PM.",
                DetectedAt = now, TimeZoneId = "UTC", ResetAt = now.AddHours(2), ContinueAt = now.AddHours(2).AddMinutes(1), Status = "Scheduled" });
            db.AgentAutoContinues.Add(new AgentAutoContinue { AgentId = id, Enabled = true, Remaining = 3, SessionId = UsageLimitState.Session(agent) });
            await db.SaveChangesAsync(ct);
        }
        manager.Reader.FailStored = true;
        using var response = await client.GetAsync($"/api/agents/{id}/limit", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var limit = (await response.Content.ReadFromJsonAsync<AgentLimitDto>(ApiJson.Options, ct))!;
        Assert.Equal("Scheduled", limit.Status);
        Assert.NotNull(limit.DetectionProblem);
        Assert.Equal(now.AddHours(2), limit.ResetAt);
        Assert.Equal(now.AddHours(2).AddMinutes(1), limit.ContinueAt);
        var policy = await client.GetFromJsonAsync<AutoContinueDto>($"/api/agents/{id}/limit/automatic", ApiJson.Options, ct);
        Assert.True(policy!.Enabled);
        Assert.Equal(3, policy.Remaining);
        manager.Reader.FailStored = false;
        var recovered = await client.GetFromJsonAsync<AgentLimitDto>($"/api/agents/{id}/limit", ApiJson.Options, ct);
        Assert.Null(recovered!.DetectionProblem);
        Assert.Equal(limit.ResetAt, recovered.ResetAt);
        Assert.NotNull(recovered.DetectedResetAt);
    }

    private sealed class Manager : WebApplicationFactory<DeveAgentManager.Api.DtoMapper>
    {
        private readonly string _data = Directory.CreateTempSubdirectory("dam-parser-api-").FullName;
        public FaultingLimitReader Reader { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DATA_DIR", _data);
            builder.UseSetting("ADMIN_PASSWORD", "parser-test-only-password");
            builder.UseSetting("PROXMOX_URL", "");
            builder.ConfigureServices(services =>
            {
                foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) &&
                    s.ImplementationType == typeof(RunnerMonitor)).ToArray()) services.Remove(service);
                services.AddSingleton<UsageLimitReader>(Reader);
            });
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Directory.Delete(_data, true);
        }
    }
}
