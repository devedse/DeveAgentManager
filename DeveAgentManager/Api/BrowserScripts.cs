namespace DeveAgentManager.Api;

/// <summary>Compiled TypeScript for pages that must run before Blazor or on the editor's separate origin.</summary>
internal static class BrowserScripts
{
    internal static string Read(string name)
    {
        using var stream = typeof(BrowserScripts).Assembly.GetManifestResourceStream($"BrowserScripts.{name}.js")
            ?? throw new InvalidOperationException($"Missing compiled browser script: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
