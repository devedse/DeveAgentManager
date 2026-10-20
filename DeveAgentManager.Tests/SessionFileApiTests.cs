// Short in-memory requests: cancelling them with the test run adds nothing.
#pragma warning disable xUnit1051

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>Downloading and deleting files agents shared, through the HTTP API. Its own Manager: sign-ins are rate limited.</summary>
public sealed class SessionFileApiTests(ApiSecurityTests.Manager manager) : IClassFixture<ApiSecurityTests.Manager>
{
    private const string AdminPassword = "test-password-1234";
    private const string Password = "file-password-1";

    [Fact]
    public async Task PdfPreviewStreamsOriginalRangesWithReadAccessAndRevokesSubsequentReads()
    {
        using var isolated = new ApiSecurityTests.Manager();
        var requests = new SessionFileApiTests(isolated);
        var (owner, ownerId) = await requests.SignedInAsNewUserAsync();
        var (reader, readerId) = await requests.SignedInAsNewUserAsync();
        var session = Guid.NewGuid();
        await using (var db = await isolated.Services.GetRequiredService<IDbContextFactory<ManagerDbContext>>().CreateDbContextAsync())
        {
            db.Sessions.Add(new StoredSession { Id = session, OwnerId = ownerId });
            await db.SaveChangesAsync();
        }
        var data = new byte[4 * 1024 * 1024];
        "%PDF-1.7\n"u8.CopyTo(data);
        var store = isolated.Services.GetRequiredService<SessionFileStore>();
        using var received = await store.ReceiveAsync(data.Length, Convert.ToHexStringLower(SHA256.HashData(data)), (s, ct) => s.WriteAsync(data, ct).AsTask(), default);
        var file = await store.CommitAsync(session, Guid.NewGuid(), received, "design.PDF", "", default);
        var path = $"/api/sessions/{session}/files/{file.Id}/preview";
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync(path)).StatusCode);
        var sharing = isolated.Services.GetRequiredService<Sharing>();
        await sharing.ShareSessionAsync(TestUsers.Plain(ownerId), session, readerId);
        var response = await reader.GetAsync(path);
        response.EnsureSuccessStatusCode();
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length < 4096);
        var preview = await response.Content.ReadFromJsonAsync<SessionFilePreview>(ApiJson.Options);
        Assert.NotNull(preview!.PdfUrl);
        Assert.Null(preview.Text); Assert.Null(preview.SourceUrl); Assert.Null(preview.HtmlUrl); Assert.Null(preview.UnavailableReason);
        var anonymous = isolated.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var range = await anonymous.SendAsync(new HttpRequestMessage(HttpMethod.Get, preview.PdfUrl) { Headers = { Range = new RangeHeaderValue(0, 31) } });
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal("application/pdf", range.Content.Headers.ContentType!.MediaType);
        Assert.Null(range.Content.Headers.ContentDisposition);
        Assert.Equal(data[..32], await range.Content.ReadAsByteArrayAsync());
        Assert.True(range.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", range.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", range.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(preview.PdfUrl.Replace("design.PDF", "other.pdf"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/sessions/{session}/files/{file.Id}")).StatusCode);
        var download = await reader.GetAsync($"/api/sessions/{session}/files/{file.Id}/download");
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal(data, await download.Content.ReadAsByteArrayAsync());
        await sharing.UnshareSessionAsync(TestUsers.Plain(ownerId), session, readerId);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(preview.PdfUrl)).StatusCode);
        var own = await owner.GetFromJsonAsync<SessionFilePreview>(path, ApiJson.Options);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/sessions/{session}/files/{file.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(own!.PdfUrl)).StatusCode);
    }

    [Fact]
    public async Task HtmlStreamsWithScopedAssetsAndRevocationAndLargeTextHasNoPreviewCap()
    {
        var (owner, ownerId) = await SignedInAsNewUserAsync();
        var (reader, readerId) = await SignedInAsNewUserAsync();
        var session = Guid.NewGuid(); var unrelated = Guid.NewGuid();
        await using (var db = await manager.Services.GetRequiredService<IDbContextFactory<ManagerDbContext>>().CreateDbContextAsync())
        {
            db.Sessions.AddRange(new StoredSession { Id = session, OwnerId = ownerId }, new StoredSession { Id = unrelated, OwnerId = ownerId });
            await db.SaveChangesAsync();
        }
        var files = manager.Services.GetRequiredService<SessionFileStore>();
        async Task<SessionFileDto> SaveAsync(Guid id, string name, byte[] data)
        {
            using var received = await files.ReceiveAsync(data.Length, Convert.ToHexStringLower(SHA256.HashData(data)), (s, ct) => s.WriteAsync(data, ct).AsTask(), default);
            return await files.CommitAsync(id, Guid.NewGuid(), received, name, "", default);
        }
        var html = Encoding.UTF8.GetBytes("<!doctype html><html><head><link rel='stylesheet' href='assets/site.css'></head><body><img src='chart.png'><script>document.body.dataset.ready='yes'</script><!--" + new string('x', 10 * 1024 * 1024) + "--></body></html>");
        var page = await SaveAsync(session, "report.html", html);
        await SaveAsync(session, "site.css", "body{color:green}"u8.ToArray());
        await SaveAsync(unrelated, "private.js", "window.private=true"u8.ToArray());
        var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1 };
        await manager.Services.GetRequiredService<SessionImageStore>().SaveAsync(session, Guid.NewGuid(), png, "chart.png", "", default);
        var previewPath = $"/api/sessions/{session}/files/{page.Id}/preview";
        var sharing = manager.Services.GetRequiredService<Sharing>();
        await sharing.ShareSessionAsync(TestUsers.Plain(ownerId), session, readerId);
        var preview = await reader.GetFromJsonAsync<SessionFilePreview>(previewPath, ApiJson.Options);
        Assert.NotNull(preview!.HtmlUrl); Assert.NotNull(preview.SourceUrl); Assert.Null(preview.Text);
        Assert.Null(preview.UnavailableReason);
        var anonymous = manager.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await anonymous.GetAsync(preview.HtmlUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(html, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("sandbox allow-scripts", csp); Assert.DoesNotContain("allow-same-origin", csp);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("cross-origin", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        var directory = preview.HtmlUrl[..(preview.HtmlUrl.LastIndexOf('/') + 1)];
        var style = await anonymous.GetAsync(directory + "assets/site.css");
        Assert.Equal("text/css", style.Content.Headers.ContentType!.MediaType);
        Assert.Equal("*", style.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal(png, await anonymous.GetByteArrayAsync(directory + "chart.png"));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(directory + "private.js")).StatusCode);
        var source = await anonymous.GetAsync(preview.SourceUrl);
        Assert.Equal("text/plain", source.Content.Headers.ContentType!.MediaType);
        Assert.Equal(html, await source.Content.ReadAsByteArrayAsync());
        var ranged = await anonymous.SendAsync(new HttpRequestMessage(HttpMethod.Get, preview.HtmlUrl) { Headers = { Range = new RangeHeaderValue(0, 19) } });
        Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
        Assert.Equal(html[..20], await ranged.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(preview.HtmlUrl.Replace("/view/", "/invalid/"))).StatusCode);
        await sharing.UnshareSessionAsync(TestUsers.Plain(ownerId), session, readerId);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(preview.HtmlUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(directory + "chart.png")).StatusCode);
        var text = Encoding.UTF8.GetBytes(new string('a', 2 * 1024 * 1024));
        var large = await SaveAsync(session, "large.log", text);
        var largePreview = await owner.GetFromJsonAsync<SessionFilePreview>($"/api/sessions/{session}/files/{large.Id}/preview", ApiJson.Options);
        Assert.Null(largePreview!.UnavailableReason); Assert.NotNull(largePreview.SourceUrl); Assert.Null(largePreview.Text);
        Assert.Equal(text, await anonymous.GetByteArrayAsync(largePreview.SourceUrl));
        var signIns = manager.Services.GetRequiredService<SignInSessions>();
        foreach (var login in await signIns.ListAsync(ownerId)) await signIns.RevokeAsync(ownerId, login.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(largePreview.SourceUrl)).StatusCode);
    }

    [Fact]
    public async Task AgentArtifactsAreVisibleAndDownloadableInEverySessionAndSurviveHistoryDeletion()
    {
        var (owner, ownerId) = await SignedInAsNewUserAsync();
        var (reader, readerId) = await SignedInAsNewUserAsync();
        var agent = Guid.NewGuid(); var runner = Guid.NewGuid(); var old = Guid.NewGuid(); var current = Guid.NewGuid();
        await using (var db = await manager.Services.GetRequiredService<IDbContextFactory<ManagerDbContext>>().CreateDbContextAsync())
        {
            db.Runners.Add(new Runner { Id = runner, OwnerId = ownerId });
            db.Agents.Add(new Agent { Id = agent, OwnerId = ownerId, RunnerId = runner });
            db.Sessions.AddRange(new StoredSession { Id = old, OwnerId = ownerId, WorkspaceAgentId = agent },
                new StoredSession { Id = current, OwnerId = ownerId, WorkspaceAgentId = agent, AgentId = agent });
            await db.SaveChangesAsync();
        }
        var files = manager.Services.GetRequiredService<SessionFileStore>();
        var data = Encoding.UTF8.GetBytes("# Shared across sessions\n");
        using var incoming = await files.ReceiveAsync(data.Length, Convert.ToHexStringLower(SHA256.HashData(data)), (s, ct) => s.WriteAsync(data, ct).AsTask(), default);
        var file = await files.CommitAsync(old, Guid.NewGuid(), incoming, "report.md", "Report", default);
        var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1 };
        var image = await manager.Services.GetRequiredService<SessionImageStore>().SaveAsync(old, Guid.NewGuid(), png, "image.png", "Image", default);
        var privateData = Encoding.UTF8.GetBytes("private");
        using var otherIncoming = await files.ReceiveAsync(privateData.Length, Convert.ToHexStringLower(SHA256.HashData(privateData)), (s, ct) => s.WriteAsync(privateData, ct).AsTask(), default);
        var privateFile = await files.CommitAsync(current, Guid.NewGuid(), otherIncoming, "private.txt", "", default);
        var sharing = manager.Services.GetRequiredService<Sharing>();
        await sharing.ShareSessionAsync(TestUsers.Plain(ownerId), old, readerId);
        Assert.Single((await reader.GetFromJsonAsync<List<SessionFileDto>>($"/api/sessions/{old}/files", ApiJson.Options))!);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/sessions/{old}/files/{privateFile.Id}/download")).StatusCode);
        await sharing.ShareAgentAsync(TestUsers.Plain(ownerId), agent, readerId, AgentAccess.Read);
        Assert.Equal(2, (await reader.GetFromJsonAsync<List<SessionFileDto>>($"/api/sessions/{current}/files", ApiJson.Options))!.Count);
        Assert.Equal(data, await reader.GetByteArrayAsync($"/api/sessions/{current}/files/{file.Id}/download"));
        Assert.Equal(png, await reader.GetByteArrayAsync($"/api/sessions/{current}/images/{image.Id}"));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/sessions/{old}")).StatusCode);
        var listed = await reader.GetFromJsonAsync<List<SessionFileDto>>($"/api/sessions/{current}/files", ApiJson.Options);
        Assert.All(listed!, f => Assert.Equal(current, f.SessionId));
        var preview = await reader.GetFromJsonAsync<SessionFilePreview>($"/api/sessions/{current}/files/{file.Id}/preview", ApiJson.Options);
        Assert.Equal(Encoding.UTF8.GetString(data), preview!.Text);
        Assert.Equal(png, await reader.GetByteArrayAsync($"/api/sessions/{current}/images/{image.Id}/download"));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/sessions/{current}/files/{file.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/sessions/{current}/files/{file.Id}/download")).StatusCode);
    }

    [Fact]
    public async Task SharedFiles_DownloadAsResumableInertAttachments_AndOnlyTheOwnerDeletesThem()
    {
        var (owner, ownerId) = await SignedInAsNewUserAsync();
        var (reader, readerId) = await SignedInAsNewUserAsync();
        var sessionId = Guid.NewGuid();
        await using (var db = await manager.Services.GetRequiredService<IDbContextFactory<ManagerDbContext>>().CreateDbContextAsync())
        {
            db.Sessions.Add(new StoredSession { Id = sessionId, OwnerId = ownerId, Title = "Build", HarnessId = "pi" });
            await db.SaveChangesAsync();
        }
        await manager.Services.GetRequiredService<Sharing>().ShareSessionAsync(TestUsers.Plain(ownerId), sessionId, readerId);
        var store = manager.Services.GetRequiredService<SessionFileStore>();
        var data = Encoding.UTF8.GetBytes("PK\u0003\u0004 a debug build of the app");
        SessionFileDto file;
        using (var incoming = await store.ReceiveAsync(data.Length, Convert.ToHexStringLower(SHA256.HashData(data)), (s, ct) => s.WriteAsync(data, ct).AsTask(), default))
        {
            file = await store.CommitAsync(sessionId, Guid.NewGuid(), incoming, "app-release.apk", "Debug build", default);
        }

        Assert.Equal([file], await reader.GetFromJsonAsync<List<SessionFileDto>>($"/api/sessions/{sessionId}/files", ApiJson.Options));
        var url = $"/api/sessions/{sessionId}/files/{file.Id}/download";
        var download = await reader.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(data, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/vnd.android.package-archive", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(("attachment", "app-release.apk"), (download.Content.Headers.ContentDisposition?.DispositionType, download.Content.Headers.ContentDisposition?.FileNameStar));
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(download.Headers.CacheControl!.NoStore);
        var previewUrl = $"/api/sessions/{sessionId}/files/{file.Id}/preview";
        var binaryPreview = await reader.GetFromJsonAsync<SessionFilePreview>(previewUrl, ApiJson.Options);
        Assert.Null(binaryPreview!.Text);
        Assert.Contains("binary", binaryPreview.UnavailableReason!);

        // An interrupted download on a phone continues where it stopped.
        var resumed = await reader.SendAsync(new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Range = new RangeHeaderValue(10, null) } });
        Assert.Equal(HttpStatusCode.PartialContent, resumed.StatusCode);
        Assert.Equal(data[10..], await resumed.Content.ReadAsByteArrayAsync());

        // Read-only: downloading, not deleting. The owner deletes it for everyone.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/sessions/{sessionId}/files/{file.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/sessions/{sessionId}/files/{file.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/sessions/{sessionId}/files/{file.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync(previewUrl)).StatusCode);
        Assert.Empty((await reader.GetFromJsonAsync<List<SessionFileDto>>($"/api/sessions/{sessionId}/files", ApiJson.Options))!);
    }

    [Fact]
    public async Task FilePreviewRequiresConversationReadPermissionAndReturnsInertUncachedText()
    {
        var (owner, ownerId) = await SignedInAsNewUserAsync();
        var (stranger, strangerId) = await SignedInAsNewUserAsync();
        var sessionId = Guid.NewGuid();
        await using (var db = await manager.Services.GetRequiredService<IDbContextFactory<ManagerDbContext>>().CreateDbContextAsync())
        {
            db.Sessions.Add(new StoredSession { Id = sessionId, OwnerId = ownerId, Title = "Preview", HarnessId = "codex" });
            await db.SaveChangesAsync();
        }
        var store = manager.Services.GetRequiredService<SessionFileStore>();
        const string markdown = "# Preview\n<script>alert('untrusted')</script>\n";
        var data = Encoding.UTF8.GetBytes(markdown);
        using var incoming = await store.ReceiveAsync(data.Length, Convert.ToHexStringLower(SHA256.HashData(data)), (s, ct) => s.WriteAsync(data, ct).AsTask(), default);
        var file = await store.CommitAsync(sessionId, Guid.NewGuid(), incoming, "report.md", "", default);
        var url = $"/api/sessions/{sessionId}/files/{file.Id}/preview";
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(url)).StatusCode);
        var anonymous = manager.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        await manager.Services.GetRequiredService<Sharing>().ShareSessionAsync(TestUsers.Plain(ownerId), sessionId, strangerId);
        var response = await stranger.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        var preview = await response.Content.ReadFromJsonAsync<SessionFilePreview>(ApiJson.Options);
        Assert.Equal(markdown, preview!.Text);
        Assert.True(preview.Markdown);
        Assert.Null(preview.UnavailableReason);
    }

    /// <summary>A new user, signed in, past choosing their own password, as the Portal (its header and origin).</summary>
    private async Task<(HttpClient Client, Guid Id)> SignedInAsNewUserAsync()
    {
        var user = await manager.Services.GetRequiredService<UserService>().CreateAsync(TestUsers.Admin, $"f{Guid.NewGuid():N}"[..16], null, AdminPassword, UserRoles.None);
        var client = manager.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAccountAsync("/account/login", Form(("username", user.Username), ("password", AdminPassword), ("returnUrl", "/")));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        client.DefaultRequestHeaders.Add(ApiHeaders.Request, "1");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        var changed = await client.PostAccountAsync("/account/password", Form(("currentPassword", AdminPassword), ("newPassword", Password), ("confirmPassword", Password), ("returnUrl", "/")));
        Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
        return (client, user.Id);
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
}
