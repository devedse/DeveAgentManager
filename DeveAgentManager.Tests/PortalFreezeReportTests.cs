#pragma warning disable xUnit1051
using System.Net;
using System.Net.Http.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Users;
using DeveAgentManager.Diagnostics;
using DeveAgentManager.Logging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace DeveAgentManager.Tests;

// The Portal's freeze watch (freeze-watch.ts) reports a page that stopped answering; the Manager writes it to its log.
public sealed class PortalFreezeReportTests(ApiSecurityTests.Manager manager) : IClassFixture<ApiSecurityTests.Manager>
{
    private static PortalFreezeReport Report(bool recovered = false, IReadOnlyList<PortalFreezeNote>? notes = null) =>
        new(recovered, 12.4, "/sessions", 3 * 3600 + 120, 210L * 1048576, 1234L * 1048576, 16234, "Mozilla/5.0 Test",
            notes ?? [new(-11.9, "click", "link → /sessions/0000"), new(-11.8, "back or forward", "/sessions"), new(-0.4, "load", "SessionList")]);

    [Fact]
    public async Task AFrozenPortalIsWrittenToTheManagersLogForAnyoneSignedIn()
    {
        var (client, username) = await SignedInAsNewUserAsync();
        var response = await client.PostAsJsonAsync("/api/diagnostics/portal-freeze", Report(), ApiJson.Options);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var entry = Assert.Single(manager.Services.GetRequiredService<ManagerLogStore>().Read(LogLevel.Warning, username + "'s browser", 10));
        Assert.Equal(nameof(LogLevel.Warning), entry.Level);
        Assert.Equal(typeof(PortalFreezeLog).FullName, entry.Category);
        Assert.Equal($"""
            The Portal stopped responding in {username}'s browser: no answer for 12 s, still frozen (page /sessions, open for 3 h 2 min).
            Memory: .NET 1,234 MB, JavaScript 210 MB, 16,234 elements on the page.
            Browser: Mozilla/5.0 Test
            What happened before, in seconds before its last answer:
            -11.9 s  click link → /sessions/0000
            -11.8 s  back or forward /sessions
            -0.4 s  load SessionList
            """.ReplaceLineEndings("\n"), entry.Message);
    }

    [Fact]
    public async Task OnlyThePortalsOwnPagesOfSomeoneSignedInCanReport()
    {
        var anonymous = manager.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        anonymous.DefaultRequestHeaders.Add(ApiHeaders.Request, "1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/diagnostics/portal-freeze", Report(), ApiJson.Options)).StatusCode);

        var (client, _) = await SignedInAsNewUserAsync();
        client.DefaultRequestHeaders.Remove(ApiHeaders.Request);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/diagnostics/portal-freeze", Report(), ApiJson.Options)).StatusCode);
    }

    [Fact]
    public void ARecoveryReportsHowLongItTookAndWhatCameAfter()
    {
        var text = PortalFreezeLog.Describe(TestUsers.Admin, Report(recovered: true, notes: [new(-1, "click", "button \"Back\""), new(3.1, "slow frame", "38000 ms")]));
        Assert.StartsWith($"The Portal stopped responding for 12 s in {TestUsers.Admin.Username}'s browser, then responded again (page /sessions", text);
        Assert.Contains("\nWhat happened, in seconds from its last answer before it stopped:\n-1.0 s  click button \"Back\"\n+3.1 s  slow frame 38000 ms", text);
    }

    [Fact]
    public void WhatTheBrowserSentStaysOnItsOwnLinesWithinLimits()
    {
        var notes = Enumerable.Range(0, 80).Select(i => new PortalFreezeNote(-i, "click\nforged", i == 79 ? new string('x', 500) : "line\r\nForged: entry")).ToList();
        var text = PortalFreezeLog.Describe(TestUsers.Admin, Report(notes: notes) with { Page = "/agents\n/forged", Browser = null, WasmBytes = null });

        var lines = text.Split('\n');
        Assert.Equal(PortalFreezeLog.MaxNotes, lines.Count(line => line.Contains(" s  click forged")));
        Assert.DoesNotContain(lines, line => line.StartsWith("Forged") || line.StartsWith("/forged"));
        Assert.Contains("(page /agents /forged,", text);
        Assert.Contains("\nMemory: JavaScript 210 MB, 16,234 elements on the page.\n", text);
        Assert.DoesNotContain("Browser:", text);
        Assert.Contains(" " + new string('x', 200) + "…", text);
        Assert.DoesNotContain(new string('x', 201), text);
    }

    [Fact]
    public void EachPersonReportsAtMostTenAnHour()
    {
        var clock = new Clock();
        var log = new PortalFreezeLog(NullLogger<PortalFreezeLog>.Instance, clock);
        var other = TestUsers.Admin with { Id = Guid.NewGuid() };
        for (var i = 0; i < PortalFreezeLog.PerHour; i++)
        {
            Assert.True(log.Record(TestUsers.Admin, Report()));
            clock.Now += TimeSpan.FromMinutes(1);
        }

        Assert.False(log.Record(TestUsers.Admin, Report()));
        Assert.True(log.Record(other, Report()));
        clock.Now += TimeSpan.FromMinutes(50); // The first is an hour old.
        Assert.True(log.Record(TestUsers.Admin, Report()));
        Assert.False(log.Record(TestUsers.Admin, Report()));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<(HttpClient Client, string Username)> SignedInAsNewUserAsync()
    {
        const string first = "test-password-1234", next = "test-freeze-password-1";
        var users = manager.Services.GetRequiredService<UserService>();
        var user = await users.CreateAsync(TestUsers.Admin, "freeze-" + Guid.NewGuid().ToString("N")[..10], null, first, UserRoles.None);
        var client = manager.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAccountAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = user.Username, ["password"] = first, ["returnUrl"] = "/" }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var changed = await client.PostAccountAsync("/account/password", new FormUrlEncodedContent(new Dictionary<string, string> { ["currentPassword"] = first, ["newPassword"] = next, ["confirmPassword"] = next, ["returnUrl"] = "/" }));
        Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
        client.DefaultRequestHeaders.Add(ApiHeaders.Request, "1");
        return (client, user.Username);
    }
}
