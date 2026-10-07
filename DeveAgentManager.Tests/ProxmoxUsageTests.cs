using System.Net;
using System.Text;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Providers.Proxmox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class ProxmoxUsageTests
{
    private static readonly RunnerMachine Machine = new(Guid.NewGuid(), "runner", "30000", "{\"Node\":\"old-node\"}", "10.0.0.2", 22);
    private const string Current = """
        {"data":{"status":"running","cpu":0.25,"mem":4294967296,"maxmem":8589934592,"uptime":200,"cpus":4,"vmid":30000}}
        """;
    private const string Resources = """
        {"data":[{"type":"qemu","vmid":30000,"node":"current-node","status":"running","cpu":0.1,"mem":4294967296,"maxmem":8589934592}]}
        """;

    [Fact]
    public async Task ReadsLiveCountersFromSingleVmEndpoint()
    {
        using var http = new Responses(path => (HttpStatusCode.OK, Current));
        var provider = Provider(http);
        var status = await provider.GetUsageAsync(Machine, TestContext.Current.CancellationToken);
        Assert.Equal(MachinePowerState.Running, status?.PowerState);
        Assert.Equal(25d, status?.CpuUsage);
        Assert.Equal(4294967296L, status?.MemoryUsedBytes);
        Assert.Single(http.Paths);
        Assert.EndsWith("/nodes/old-node/qemu/30000/status/current", http.Paths[0]);
    }

    [Fact]
    public async Task RecoversWhenVmHasMovedSinceCreation_ThenReusesCurrentNode()
    {
        using var http = new Responses(path => path.Contains("/old-node/")
            ? (HttpStatusCode.InternalServerError, "{\"data\":null}")
            : path.Contains("/cluster/resources") ? (HttpStatusCode.OK, Resources) : (HttpStatusCode.OK, Current));
        var provider = Provider(http);
        var first = await provider.GetUsageAsync(Machine, TestContext.Current.CancellationToken);
        var second = await provider.GetUsageAsync(Machine, TestContext.Current.CancellationToken);
        Assert.Equal(25d, first?.CpuUsage);
        Assert.Equal(25d, second?.CpuUsage);
        Assert.Equal(4, http.Paths.Count);
        Assert.EndsWith("/nodes/current-node/qemu/30000/status/current", http.Paths[^1]);
    }

    [Fact]
    public async Task UsesNodeLearnedByFullStatus_InsteadOfCreationNode()
    {
        using var http = new Responses(path => path.Contains("/cluster/resources")
            ? (HttpStatusCode.OK, Resources) : (HttpStatusCode.OK, Current));
        var provider = Provider(http);
        await provider.GetStatusAsync(Machine, TestContext.Current.CancellationToken);
        http.Paths.Clear();

        Assert.Equal(25d, (await provider.GetUsageAsync(Machine, TestContext.Current.CancellationToken))?.CpuUsage);
        Assert.Single(http.Paths);
        Assert.EndsWith("/nodes/current-node/qemu/30000/status/current", http.Paths[0]);
    }

    [Fact]
    public async Task BackgroundSamplerPublishesChangingReadingsEverySecondThroughApi()
    {
        await using var manager = new SamplingManager();
        var client = manager.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var ct = TestContext.Current.CancellationToken;
        using var login = await client.PostAccountAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "admin", ["password"] = "usage-test-password", ["returnUrl"] = "/"
        }), ct);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        await using (var db = await manager.Services.GetRequiredService<IDbContextFactory<ManagerDbContext>>().CreateDbContextAsync(ct))
        {
            db.Runners.Add(new Runner { OwnerId = User.BootstrapId, Id = Machine.RunnerId, Name = "usage-runner", ProviderId = "proxmox",
                State = RunnerState.Ready, MachineId = Machine.MachineId, ProviderData = Machine.ProviderData,
                Host = Machine.Host, Username = "test" });
            await db.SaveChangesAsync(ct);
        }

        using var monitor = ActivatorUtilities.CreateInstance<RunnerUsageMonitor>(manager.Services);
        await monitor.StartAsync(ct);
        try
        {
            List<RunnerUsageSample> samples = [];
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            while (samples.Count < 4)
            {
                using var response = await client.GetAsync($"/api/runners/{Machine.RunnerId}/usage?minutes=1", deadline.Token);
                response.EnsureSuccessStatusCode();
                samples = System.Text.Json.JsonSerializer.Deserialize<List<RunnerUsageSample>>(
                    await response.Content.ReadAsStringAsync(deadline.Token), ApiJson.Options)!;
                if (samples.Count < 4) await Task.Delay(100, deadline.Token);
            }

            Assert.True(samples[^1].At - samples[0].At < TimeSpan.FromSeconds(5));
            Assert.Equal(samples.Count, samples.Select(s => s.CpuPercent).Distinct().Count());
            Assert.All(samples.Zip(samples.Skip(1)), pair =>
                Assert.InRange((pair.Second.At - pair.First.At).TotalSeconds, 0.5, 2));
        }
        finally { await monitor.StopAsync(CancellationToken.None); }
    }

    private sealed class SamplingManager : WebApplicationFactory<DeveAgentManager.Api.DtoMapper>
    {
        private readonly string _data = Directory.CreateTempSubdirectory("dam-usage-test-").FullName;
        private int _reading;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DATA_DIR", _data);
            builder.UseSetting("ADMIN_PASSWORD", "usage-test-password");
            builder.UseSetting("PROXMOX_URL", "https://proxmox.test:8006");
            builder.UseSetting("PROXMOX_TOKEN_ID", "test@pve!manager");
            builder.UseSetting("PROXMOX_TOKEN_SECRET", "test");
            builder.ConfigureServices(services =>
            {
                // Start the real usage monitor after inserting a ready runner; exclude the slow status monitor.
                foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) &&
                    (s.ImplementationType == typeof(RunnerMonitor) || s.ImplementationType == typeof(RunnerUsageMonitor))).ToArray())
                    services.Remove(service);
                services.AddHttpClient(ProxmoxProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() =>
                    new Responses(_ => (HttpStatusCode.OK, Current.Replace("0.25", $"0.{Interlocked.Increment(ref _reading):D2}"))));
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Directory.Delete(_data, recursive: true);
        }
    }

    private static ProxmoxProvider Provider(Responses http) => new(
        new ProxmoxOptions { Host = "proxmox.test", TokenId = "test@pve!manager", TokenSecret = "test" },
        http, NullLogger<ProxmoxProvider>.Instance);

    private sealed class Responses(Func<string, (HttpStatusCode Status, string Json)> respond) : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Paths { get; } = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            var (status, json) = respond(path);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
