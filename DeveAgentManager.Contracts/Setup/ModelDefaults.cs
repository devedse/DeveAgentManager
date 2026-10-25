namespace DeveAgentManager.Core.Setup;

/// <summary>
/// A user's own default model and effort for one harness (My settings → Models). An empty field follows the Manager's
/// default (Manager settings), also when that changes later.
/// </summary>
public sealed class ModelDefault
{
    public const int MaxModelLength = 200;

    public string? Model { get; set; }

    public string? Effort { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Model) && string.IsNullOrWhiteSpace(Effort);

    /// <summary>Why this model name can't be passed to the harness, or null. Empty is fine: the Manager's default.</summary>
    public static string? ModelProblem(string? model) =>
        string.IsNullOrWhiteSpace(model) ? null
        : model.Trim().Length > MaxModelLength ? $"At most {MaxModelLength} characters."
        : model.Trim().Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ? "One model name, without spaces."
        : null;

    /// <summary>Why this effort level can't be passed to the harness, or null. Empty is fine: the Manager's default.</summary>
    public static string? EffortProblem(string? effort) =>
        string.IsNullOrWhiteSpace(effort) || effort.Trim() is { Length: <= 40 } level && level.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? null : "A level like high.";

    /// <summary>Throws with a readable message when something can't be used.</summary>
    public void Validate(string harnessName)
    {
        if ((ModelProblem(Model) ?? EffortProblem(Effort)) is { } problem)
        {
            throw new ArgumentException($"{harnessName}: {problem}");
        }
    }

    public ModelDefault Cleaned() => new() { Model = Clean(Model), Effort = Clean(Effort) };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// What an agent's conversations start with in one harness: its owner's <see cref="ModelDefault"/>, else the Manager's
/// default. Null fields let the harness choose. pi's provider only comes with the Manager's model.
/// </summary>
public sealed record EffectiveModel(string? Provider, string? Model, string? Effort)
{
    public static EffectiveModel Resolve(HarnessConfig manager, ModelDefault? mine)
    {
        var own = mine?.Cleaned();
        var managerModel = manager.DefaultModel is { } model && !string.IsNullOrWhiteSpace(model) ? model.Trim() : null;
        var effort = manager.DefaultThinkingLevel is { } level && !string.IsNullOrWhiteSpace(level) ? level.Trim() : null;
        return own?.Model is { } chosen
            ? new EffectiveModel(null, chosen, own.Effort ?? effort)
            : new EffectiveModel(managerModel is null || string.IsNullOrWhiteSpace(manager.DefaultProvider) ? null : manager.DefaultProvider.Trim(), managerModel, own?.Effort ?? effort);
    }
}
