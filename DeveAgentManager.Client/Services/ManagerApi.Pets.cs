using DeveAgentManager.Contracts.Pets;
namespace DeveAgentManager.Client.Services;
public sealed partial class ManagerApi
{
    public Task<MyPetDto> MyPetAsync() => GetAsync<MyPetDto>("api/me/pet");
    public Task<MyPetDto> SavePetAsync(PetPreferences value) => SendAsync<MyPetDto>(HttpMethod.Put, "api/me/pet", value);
    public Task<MyPetDto> AdoptPetAsync(PetAdoption value) => SendAsync<MyPetDto>(HttpMethod.Post, "api/me/pet/adopt", value);
    public Task<MyPetDto> RetirePetAsync(PetRetirement value) => SendAsync<MyPetDto>(HttpMethod.Post, "api/me/pet/retire", value);
    public Task PetActionAsync(string action) => SendAsync(HttpMethod.Post, "api/me/pet/interact", new PetInteractionRequest(action));
    public Task SnoozePetAsync(int minutes) => SendAsync(HttpMethod.Post, "api/me/pet/snooze", new PetSnoozeRequest(minutes));
    public Task<PetOptions> PetOptionsAsync() => GetAsync<PetOptions>("api/pets/settings");
    public Task<PetOptions> SavePetOptionsAsync(PetOptions value) => SendAsync<PetOptions>(HttpMethod.Put, "api/pets/settings", value);
    public Task<List<PetPersonDto>> PetPeopleAsync() => GetAsync<List<PetPersonDto>>("api/pets/people");
    public Task AdjustPetAsync(Guid id, PetAdjustment value) => SendAsync(HttpMethod.Put, $"api/pets/people/{id}", value);
    public Task SetAllPetsAsync(bool enabled) => SendAsync(HttpMethod.Post, "api/pets/people", new PetBulkRequest(enabled));
}
