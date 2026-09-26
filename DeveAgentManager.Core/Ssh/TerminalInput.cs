using System.Text.RegularExpressions;

namespace DeveAgentManager.Core.Ssh;

/// <summary>Tells what a person typed apart from what the terminal answers by itself.</summary>
public static partial class TerminalInput
{
    /// <summary>
    /// Replies xterm.js sends on its own when the program asks (tmux does on every attach): device attributes, cursor
    /// position, status and focus reports, and OSC/DCS answers. Anything else (keys, pastes, mouse clicks and scrolls) is
    /// the user.
    /// </summary>
    public static bool IsTerminalReply(string data) => Reply().IsMatch(data);

    [GeneratedRegex(@"^(\x1b\[[?>=]?[0-9;]*[cRnIOt]|\x1b\[\?[0-9;]*\$y|\x1b\][^\x07\x1b]*(\x07|\x1b\\)|\x1bP[^\x1b]*\x1b\\)+$")]
    private static partial Regex Reply();
}
