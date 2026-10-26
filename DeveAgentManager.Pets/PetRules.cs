using System.Text.Json;
using DeveAgentManager.Contracts.Pets;

namespace DeveAgentManager.Pets;

/// <summary>New species are catalog entries plus renderer modules; progression is shared.</summary>
public sealed class PetCatalog
{
    public IReadOnlyList<PetDefinition> All { get; }
    public PetCatalog()
    {
        using var stream = typeof(PetCatalog).Assembly.GetManifestResourceStream("DeveAgentManager.Pets.catalog.json")!;
        All = JsonSerializer.Deserialize<PetDefinition[]>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if (All.Count == 0 || All.Select(x => x.Id).Distinct().Count() != All.Count || All.Any(x => x.Stages.Length != 4))
            throw new InvalidOperationException("Pet definitions need unique ids and four growth stages.");
    }
    public PetDefinition Require(string id) => All.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException("Choose a pet from the catalog.");
}

public static class PetRules
{
    public const int MaxLevel = 1000;
    public const long GrowthScale = 500_000;
    public static long LevelStart(int level) => GrowthScale * (Math.Clamp(level, 1, MaxLevel) - 1) * Math.Clamp(level, 1, MaxLevel);
    public static int Level(long experience) => Math.Clamp((int)((1 + Math.Sqrt(1 + 4 * Math.Max(0, experience) / (double)GrowthScale)) / 2), 1, MaxLevel);
    public static int Stage(int level) => level < 6 ? 1 : level < 10 ? 2 : 3;
    public static double StageProgress(long experience)
    {
        var level = Level(experience); var stage = Stage(level);
        var low = LevelStart(new[] { 1, 1, 6, 10 }[stage]);
        var high = stage == 3 ? low : LevelStart(new[] { 1, 6, 10, 10 }[stage]);
        return stage == 3 ? 1 : Math.Clamp((experience - low) / (double)(high - low), 0, 1);
    }
    public static string CleanName(string name, bool allowEmpty = false)
    {
        var clean = (name ?? "").Trim();
        if ((!allowEmpty && clean.Length == 0) || clean.Length > 40 || clean.Any(char.IsControl)) throw new ArgumentException("Use a pet name of 1–40 characters.");
        return clean;
    }
}

public sealed class UserPet
{
    public Guid UserId { get; set; }
    public bool Enabled { get; set; }
    public string Species { get; set; } = "";
    public string Name { get; set; } = "";
    public long Experience { get; set; }
    public long LifetimeTokens { get; set; }
    public long Generation { get; set; }
    public int Happiness { get; set; } = 70;
    public DateTime? SnoozedUntil { get; set; }
    public bool ReducedMotion { get; set; }
    public int Size { get; set; } = 140;
    public long Revision { get; set; }
    public string Reaction { get; set; } = "idle";
    public DateTime? ReactionAt { get; set; }
    public DateTime? LastInteractionAt { get; set; }
}

/// <summary>A high-water mark per original native turn, shared by copies and workspace moves.</summary>
public sealed class PetUsageCredit
{
    public string Source { get; set; } = "";
    public string PromptId { get; set; } = "";
    public long Tokens { get; set; }
    public Guid? UserId { get; set; }
    public long Generation { get; set; }
}
public sealed class PetInputClaim
{
    public long Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid SessionId { get; set; }
    public Guid UserId { get; set; }
    public long Generation { get; set; }
    public DateTime At { get; set; }
}
