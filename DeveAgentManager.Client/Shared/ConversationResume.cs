using DeveAgentManager.Client.Dialogs;
using DeveAgentManager.Client.Services;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using MudBlazor;

namespace DeveAgentManager.Client.Shared;

/// <summary>Continuing a saved conversation from the history pages.</summary>
public static class ConversationResume
{
    /// <summary>
    /// In its project, the choice of resuming it in the workspace it last ran in (or Main) or in a new workspace. Without a
    /// project it can use, a new agent continues it on a Runner.
    /// </summary>
    public static async Task ContinueAsync(ManagerApi api, IDialogService dialogs, SessionDto session, IReadOnlyList<SessionDto>? sessions = null)
    {
        if (session.ProjectId is { } project && session.WorkspaceAccess >= AgentAccess.Write)
        {
            var agents = (await api.ListAgentsAsync()).Where(a => WorkspaceGroups.ProjectKey(a) == project && a.Access >= AgentAccess.Read).ToList();
            if ((agents.FirstOrDefault(a => a.Id == session.WorkspaceAgentId) ?? agents.FirstOrDefault(a => !a.IsClone) ?? agents.FirstOrDefault()) is { } here)
            {
                var title = (sessions ?? await api.ListSessionsAsync()).FirstOrDefault(s => s.AgentId == here.Id)?.Title;
                await ConversationChooserDialog.ShowAsync(dialogs, session, here, agents, title);
                return;
            }
        }
        await dialogs.ShowAsync<NewAgentDialog>("Continue session", new DialogParameters<NewAgentDialog> { { d => d.ContinueSession, session } });
    }
}
