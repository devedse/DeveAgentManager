namespace DeveAgentManager.Core.Agents;

/// <summary>A lifecycle event from the session belonging to the pane's running process.</summary>
public sealed record NativeTurnObservation(string SessionId, string TurnId, string State, DateTime At);

internal static class NativeActivityProbe
{
    internal static readonly string Script = ReadScript();
    private static string ReadScript()
    {
        using var stream = typeof(NativeActivityProbe).Assembly.GetManifestResourceStream("DeveAgentManager.Core.Scripts.agent-activity.py")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
