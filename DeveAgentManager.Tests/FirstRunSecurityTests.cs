#pragma warning disable xUnit1051
using DeveAgentManager.Api;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class FirstRunSecurityTests
{
    private sealed class Manager : WebApplicationFactory<DtoMapper>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
            .UseSetting("DATA_DIR", Directory.CreateTempSubdirectory("dam-first-start-").FullName)
            .UseSetting("ADMIN_PASSWORD", "").UseSetting("PROXMOX_URL", "");
    }
    [Fact]
    public async Task FirstStartUsesUnknownSetupCredentialAndNormalRestartsPreserveChosenPassword()
    {
        using var manager = new Manager();
        var users = manager.Services.GetRequiredService<UserService>();
        var initial = (await users.GetAsync(User.BootstrapId))!;
        Assert.True(initial.MustChangePassword);
        Assert.Equal(PasswordVerificationResult.Failed, new PasswordHasher<User>().VerifyHashedPassword(initial, initial.PasswordHash, ManagerOptions.DefaultAdminPassword));
        var stamp = initial.SecurityStamp;
        await users.EnsureAdminAsync();
        Assert.Equal(stamp, (await users.GetAsync(initial.Id))!.SecurityStamp);
        await users.ResetPasswordAsync(TestUsers.Admin, initial.Id, "chosen-password-1234");
        var chosen = (await users.GetAsync(initial.Id))!;
        await users.EnsureAdminAsync();
        var restarted = (await users.GetAsync(initial.Id))!;
        Assert.Equal(chosen.PasswordHash, restarted.PasswordHash);
        Assert.Equal(chosen.SecurityStamp, restarted.SecurityStamp);
        Assert.False(restarted.MustChangePassword);
        Assert.DoesNotContain(ManagerOptions.DefaultAdminPassword, await manager.CreateClient().GetStringAsync("/login"));
    }
}
