using System.Net;
using System.Net.Http.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Contracts.Pets;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace DeveAgentManager.Tests;

public sealed class PetApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Password = "test-only-pets-api-1234";
    [Fact] public async Task DamagochiManagerIsIndependentAndOrdinaryUsersCannotManageOtherPets()
    {
        var folder = Directory.CreateTempSubdirectory("dam-pets-api-").FullName;
        try {
            await using var manager = new Manager(folder);
            using var plain = await Login(manager, UserRoles.None);
            using var keeper = await Login(manager, UserRoles.DamagochiManager);
            Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/pets/people", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await keeper.GetAsync("/api/users", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await keeper.GetAsync("/api/settings/profile", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await keeper.PutAsJsonAsync("/api/pets/settings", new PetOptions(true), ApiJson.Options, Ct)).StatusCode);
            var mine = (await plain.GetFromJsonAsync<MyPetDto>("/api/me/pet", ApiJson.Options, Ct))!;
            Assert.True(mine.CanEdit); Assert.False(mine.CanManage);
            Assert.True(mine.Pet.NeedsAdoption); Assert.Equal("", mine.Pet.Name);
            Assert.Equal(HttpStatusCode.OK, (await plain.PostAsJsonAsync("/api/me/pet/adopt", new PetAdoption("Byte", "silkie", 0), ApiJson.Options, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await plain.PutAsJsonAsync("/api/me/pet", new PetPreferences(true, "Snek", "snek", Generation: 1), ApiJson.Options, Ct)).StatusCode);
            var path = "/api/pets/people/" + mine.Pet.UserId;
            var adjustment = new PetAdjustment(true, "Byte", "silkie", 10, 91);
            Assert.Equal(HttpStatusCode.Forbidden, (await plain.PutAsJsonAsync(path, adjustment, ApiJson.Options, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await keeper.PutAsJsonAsync(path, adjustment, ApiJson.Options, Ct)).StatusCode);
            var changed = (await plain.GetFromJsonAsync<MyPetDto>("/api/me/pet", ApiJson.Options, Ct))!;
            Assert.Equal(("Byte", 10, 91), (changed.Pet.Name, changed.Pet.Level, changed.Pet.Happiness));
            Assert.Equal(HttpStatusCode.NoContent, (await plain.PostAsJsonAsync("/api/me/pet/snooze", new PetSnoozeRequest(5), ApiJson.Options, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await plain.PostAsJsonAsync("/api/me/pet/snooze", new PetSnoozeRequest(-1), ApiJson.Options, Ct)).StatusCode);
            plain.DefaultRequestHeaders.Remove(ApiHeaders.Request);
            Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsJsonAsync("/api/me/pet/retire", new PetRetirement(1), ApiJson.Options, Ct)).StatusCode);
            plain.DefaultRequestHeaders.Add(ApiHeaders.Request, "1");
            Assert.Equal(HttpStatusCode.OK, (await plain.PostAsJsonAsync("/api/me/pet/retire", new PetRetirement(1), ApiJson.Options, Ct)).StatusCode);
            var egg = (await plain.GetFromJsonAsync<MyPetDto>("/api/me/pet", ApiJson.Options, Ct))!;
            Assert.True(egg.Pet.NeedsAdoption);
            Assert.Equal(HttpStatusCode.OK, (await plain.PostAsJsonAsync("/api/me/pet/adopt", new PetAdoption("New", "snek", egg.Pet.Generation), ApiJson.Options, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await plain.PostAsJsonAsync("/api/me/pet/retire", new PetRetirement(1), ApiJson.Options, Ct)).StatusCode);
            await keeper.PutAsJsonAsync("/api/pets/settings", new PetOptions(false), ApiJson.Options, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, (await plain.PutAsJsonAsync("/api/me/pet", new PetPreferences(false, "Byte", "silkie", Generation: 1), ApiJson.Options, Ct)).StatusCode);
            keeper.DefaultRequestHeaders.Remove(ApiHeaders.Request);
            Assert.Equal(HttpStatusCode.Forbidden, (await keeper.PutAsJsonAsync("/api/pets/settings", new PetOptions(true), ApiJson.Options, Ct)).StatusCode);
        } finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }
    private sealed class Manager(string folder) : WebApplicationFactory<DeveAgentManager.Api.DtoMapper> {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("DATA_DIR", folder).UseSetting("ADMIN_PASSWORD", Password).UseSetting("CPU_TRACE_STARTUP", "false").UseSetting("PROXMOX_URL", "");
    }
    private static async Task<HttpClient> Login(Manager manager, UserRoles roles) {
        var user = await manager.Services.GetRequiredService<UserService>().CreateAsync(TestUsers.Admin, "pet-" + Guid.NewGuid().ToString("N")[..10], null, Password, roles, Ct);
        var client = manager.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAccountAsync("/account/login", Form(("username", user.Username), ("password", Password), ("returnUrl", "/")))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAccountAsync("/account/password", Form(("currentPassword", Password), ("newPassword", Password + "-new"), ("confirmPassword", Password + "-new"), ("returnUrl", "/")))).StatusCode);
        client.DefaultRequestHeaders.Add(ApiHeaders.Request, "1"); return client;
    }
    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) => new(fields.ToDictionary(x => x.Key, x => x.Value));
}
