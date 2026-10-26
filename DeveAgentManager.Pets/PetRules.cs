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
    public static long LevelStart(int level) => 25_000L * (Math.Clamp(level, 1, MaxLevel) - 1) * Math.Clamp(level, 1, MaxLevel);
    public static int Level(long experience) => Math.Clamp((int)((1 + Math.Sqrt(1 + 4 * Math.Max(0, experience) / 25_000d)) / 2), 1, MaxLevel);
    public static int Stage(int level) => level < 3 ? 0 : level < 6 ? 1 : level < 10 ? 2 : 3;
    public static double StageProgress(long experience)
    {
        var level = Level(experience); var stage = Stage(level);
        var low = LevelStart(new[] { 1, 3, 6, 10 }[stage]);
        var high = stage == 3 ? low : LevelStart(new[] { 3, 6, 10, 10 }[stage]);
        return stage == 3 ? 1 : Math.Clamp((experience - low) / (double)(high - low), 0, 1);
    }
    public static string CleanName(string name)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length is < 1 or > 40 || clean.Any(char.IsControl)) throw new ArgumentException("Use a pet name of 1–40 characters.");
        return clean;
    }
}

public sealed class UserPet
{
    public Guid UserId { get; set; }
    public bool Enabled { get; set; }
    public string Species { get; set; } = "snek";
    public string Name { get; set; } = "Snek";
    public long Experience { get; set; }
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
}
public sealed class PetInputClaim
{
    public long Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid SessionId { get; set; }
    public Guid UserId { get; set; }
    public DateTime At { get; set; }
}
