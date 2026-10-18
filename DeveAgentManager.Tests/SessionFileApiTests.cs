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
