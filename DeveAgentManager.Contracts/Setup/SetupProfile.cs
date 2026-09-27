namespace DeveAgentManager.Core.Setup;

/// <summary>
/// What gets installed and configured on every Runner on top of the base setup: environment variables
/// (API keys), files (logins, settings, plugins) and an extra script. Stored encrypted.
/// </summary>
public sealed class SetupProfile
{
    public string? GitUserName { get; set; }

    public string? GitUserEmail { get; set; }

    public List<SetupEnvironmentVariable> EnvironmentVariables { get; set; } = [];

    public List<SetupFile> Files { get; set; } = [];

    /// <summary>Bash run as the Runner Account at the end of setup, e.g. <c>pi install npm:some-package</c>.</summary>
    public string? Script { get; set; }

    /// <summary>Packages, default model and settings per harness id (e.g. "pi"), for harnesses that support it.</summary>
    public Dictionary<string, HarnessConfig> Harnesses { get; set; } = [];

    public HarnessConfig GetHarness(string harnessId)
    {
        if (!Harnesses.TryGetValue(harnessId, out var config))
        {
            config = new HarnessConfig();
            Harnesses[harnessId] = config;
        }

        return config;
    }

    /// <summary>A name bash can export: letters, digits and underscores, not starting with a digit.</summary>
    public static bool IsValidVariableName(string name) =>
        name.Length > 0 && !char.IsAsciiDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>Why this variable can't be used, or null. An empty row is fine: it is left out when saving.</summary>
    public string? Problem(SetupEnvironmentVariable variable)
    {
        var name = variable.Name.Trim();
        if (name.Length == 0)
        {
            return variable.Value.Length == 0 ? null : "Enter a name for this value.";
        }

        if (!IsValidVariableName(name))
        {
            return "Letters, digits and _ only, not starting with a digit.";
        }

        return EnvironmentVariables.Count(v => v.Name.Trim() == name) > 1 ? $"{name} is listed twice." : null;
    }

    /// <summary>Why this file can't be written, or null. An empty row is fine: it is left out when saving.</summary>
    public string? PathProblem(SetupFile file)
    {
        var path = file.Path.Trim();
        if (path.Length == 0)
        {
            return file.Content.Length == 0 ? null : "Enter where the file goes on the Runner.";
        }

        if (!path.StartsWith('/') && !path.StartsWith("~/", StringComparison.Ordinal))
        {
            return "A full path, or one in the home folder like ~/.pi/agent/AGENTS.md.";
        }

        return Files.Count(f => f.Path.Trim() == path) > 1 ? "This file is listed twice." : null;
    }

    public static string? ModeProblem(SetupFile file) =>
        file.Mode.Trim() is { Length: 3 or 4 } mode && mode.All(c => c is >= '0' and <= '7') ? null : "Octal, like 600 or 644.";

    /// <summary>Throws with a readable message for the first thing that can't be applied.</summary>
    public void Validate()
    {
        foreach (var variable in EnvironmentVariables)
        {
            if (Problem(variable) is { } problem)
            {
                throw new ArgumentException($"Environment variable '{variable.Name.Trim()}': {problem}");
            }
        }

        foreach (var file in Files.Where(f => f.Path.Trim().Length > 0 || f.Content.Length > 0))
        {
            if ((PathProblem(file) ?? ModeProblem(file)) is { } problem)
            {
                throw new ArgumentException($"File '{file.Path.Trim()}': {problem}");
            }
        }

        foreach (var config in Harnesses.Values)
        {
            config.Validate();
        }
    }
}

/// <summary>
/// What a harness gets on every Runner: packages (pi: extensions, skills, themes), the model it starts with and extra
/// settings. Applied at setup and with "Apply to Runners". Packages removed here are removed from the Runners too.
/// </summary>
public sealed class HarnessConfig
{
    /// <summary>Package sources as the harness's install command takes them, e.g. <c>npm:pi-web-access</c>.</summary>
    public List<string> Packages { get; set; } = [];

    public string? DefaultProvider { get; set; }

    public string? DefaultModel { get; set; }

    public string? DefaultThinkingLevel { get; set; }

    /// <summary>A JSON object merged into the harness's settings file (objects merge, other values replace).</summary>
    public string? Settings { get; set; }

    /// <summary>A JSON object merged into the harness's keybindings file (pi: ~/.pi/agent/keybindings.json).</summary>
    public string? Keybindings { get; set; }

    /// <summary>Package sources without blanks and duplicates, in order.</summary>
    public IReadOnlyList<string> CleanPackages() =>
        Packages.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Why this package source can't be installed, or null.</summary>
    public static string? PackageProblem(string package) =>
        package.Trim().Any(char.IsWhiteSpace) ? "No spaces: a source like npm:pi-web-access or git:github.com/owner/repo." : null;

    /// <summary>Throws with a readable message when something can't be applied.</summary>
    public void Validate()
    {
        foreach (var package in CleanPackages())
        {
            if (PackageProblem(package) is { } problem)
            {
                throw new ArgumentException($"Package '{package}': {problem}");
            }
        }

        ValidateJsonObject(Settings, "Extra settings", "{ \"hideThinkingBlock\": true }");
        ValidateJsonObject(Keybindings, "Keybindings", "{ \"app.clipboard.pasteImage\": [] }");
    }

    private static void ValidateJsonObject(string? json, string what, string example)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject)
            {
                throw new ArgumentException($"{what} must be a JSON object, like {example}.");
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ArgumentException($"{what} are not valid JSON: {ex.Message}");
        }
    }

    /// <summary>The keybindings to merge (empty object when none are set).</summary>
    public System.Text.Json.Nodes.JsonObject BuildKeybindings() =>
        string.IsNullOrWhiteSpace(Keybindings)
            ? new System.Text.Json.Nodes.JsonObject()
            : (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(Keybindings)!;

    /// <summary>The settings to merge: the extra settings plus the default model fields that are filled in.</summary>
    public System.Text.Json.Nodes.JsonObject BuildSettings()
    {
        var settings = string.IsNullOrWhiteSpace(Settings)
            ? new System.Text.Json.Nodes.JsonObject()
            : (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(Settings)!;
        Set("defaultProvider", DefaultProvider);
        Set("defaultModel", DefaultModel);
        Set("defaultThinkingLevel", DefaultThinkingLevel);
        return settings;

        void Set(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                settings[name] = value.Trim();
            }
        }
    }
}

public sealed class SetupEnvironmentVariable
{
    public string Name { get; set; } = "";

    public string Value { get; set; } = "";
}

public sealed class SetupFile
{
    /// <summary>Path on the Runner; "~/" means the Runner Account's home.</summary>
    public string Path { get; set; } = "";

    public string Content { get; set; } = "";

    /// <summary>Octal file mode.</summary>
    public string Mode { get; set; } = "600";
}
