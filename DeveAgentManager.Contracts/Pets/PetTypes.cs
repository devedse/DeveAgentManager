namespace DeveAgentManager.Contracts.Pets;

public sealed record PetDefinition(string Id, string Name, string DefaultName, string Renderer, string[] Stages);
public sealed record PetOptions(bool Enabled = false, bool DefaultForNewUsers = false);
public sealed record PetStateDto(Guid UserId, string Name, string Species, bool Enabled, long Experience, int Level,
    int Stage, string StageName, long LevelStart, long NextLevel, double StageProgress, int Happiness,
    DateTime? SnoozedUntil, bool ReducedMotion, int Size, long Revision, string Reaction, DateTime? ReactionAt,
    long LifetimeTokens, long Generation)
{
    public bool NeedsAdoption => string.IsNullOrEmpty(Species);
    public double LevelProgress => NextLevel <= LevelStart ? 100 : Math.Clamp((Experience - LevelStart) * 100d / (NextLevel - LevelStart), 0, 100);
}
public sealed record MyPetDto(PetOptions Options, bool CanManage, bool CanEdit, PetStateDto Pet, IReadOnlyList<PetDefinition> Catalog);
public sealed record PetPreferences(bool Enabled, string Name, string Species, bool ReducedMotion = false, int Size = 140, long Generation = 0);
public sealed record PetAdoption(string Name, string Species, long Generation);
public sealed record PetRetirement(long Generation);
public sealed record PetAdjustment(bool Enabled, string Name, string Species, int Level, int Happiness, long? Experience = null, long? LifetimeTokens = null);
public sealed record PetPersonDto(Guid UserId, string DisplayName, string Username, PetStateDto Pet);
public sealed record PetBulkRequest(bool Enabled);
public sealed record PetSnoozeRequest(int Minutes);
public sealed record PetInteractionRequest(string Action);
/// <summary>Small usage metadata for one real user turn. No prompts or attachment contents.</summary>
public sealed class SessionUsageTurn
{
    public string PromptId { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public long Tokens { get; set; }
}
