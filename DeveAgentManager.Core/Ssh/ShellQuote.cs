namespace DeveAgentManager.Core.Ssh;

public static class ShellQuote
{
    /// <summary>Quotes a value so a POSIX shell sees it as one literal word.</summary>
    public static string Quote(string value)
    {
        if (value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '@' or '=' or ',' or '+'))
        {
            return value;
        }

        return "'" + value.Replace("'", "'\"'\"'") + "'";
    }

    /// <summary>
    /// Turns a remote path into a shell word. A leading "~/" becomes "$HOME/" so it is expanded even when quoted.
    /// </summary>
    public static string Path(string path)
    {
        if (path == "~")
        {
            return "\"$HOME\"";
        }

        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            var rest = path[2..];
            return rest.Length == 0 ? "\"$HOME\"" : "\"$HOME\"/" + Quote(rest);
        }

        return Quote(path);
    }
}
