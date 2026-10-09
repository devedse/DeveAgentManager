using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using MudBlazor;

namespace DeveAgentManager.Client.Shared;

public enum Tone
{
    Neutral,
    Info,
    Success,
    Warning,
    Error,

    /// <summary>Running, but what it is doing can't be told from its screen; its own color, so it never looks stopped.</summary>
    Unclear,
}

public sealed record StateStyle(string Text, Tone Tone, bool Busy = false);

/// <summary>How Runner and agent states look in the Portal.</summary>
public static class StateStyles
{
    public static StateStyle For(RunnerState state) => state switch
    {
        RunnerState.Creating => new("Creating", Tone.Info, true),
        RunnerState.Starting => new("Starting", Tone.Info, true),
        RunnerState.WaitingForNetwork => new("Connecting", Tone.Info, true),
        RunnerState.SettingUp => new("Setting up", Tone.Info, true),
        RunnerState.Ready => new("Ready", Tone.Success),
        RunnerState.Stopping => new("Stopping", Tone.Warning, true),
        RunnerState.Stopped => new("Stopped", Tone.Neutral),
        RunnerState.Deleting => new("Deleting", Tone.Warning, true),
        RunnerState.Error => new("Error", Tone.Error),
        _ => new(state.ToString(), Tone.Neutral),
    };

    public static StateStyle For(AgentState state) => state switch
    {
        AgentState.Starting => new("Starting", Tone.Info, true),
        AgentState.Running => new("Running", Tone.Success, true),
        AgentState.Exited => new("Exited", Tone.Warning),
        AgentState.Stopped => new("Stopped", Tone.Neutral),
        AgentState.Error => new("Error", Tone.Error),
        _ => new("Unknown", Tone.Neutral),
    };

    /// <summary>Process state is separate from observed activity; legacy clients can still use the Working flag.</summary>
    public static StateStyle For(AgentDto agent) => agent.State switch
    {
        AgentState.Running when agent.Activity is { } activity => activity.Status switch
        {
            AgentActivityStatus.Working => new("Working", Tone.Info, true),
            AgentActivityStatus.Ready => new("Ready", Tone.Success),
            AgentActivityStatus.NeedsInput => new("Needs input", Tone.Warning),
            AgentActivityStatus.UsageLimited => new("Usage limit", Tone.Warning),
            _ => new("Unclear", Tone.Unclear),
        },
        AgentState.Running when agent.Working => new("Working", Tone.Info, true),
        AgentState.Running => new("Idle", Tone.Success),
        _ => For(agent.State),
    };

    public static string CssClass(this Tone tone) => tone.ToString().ToLowerInvariant();

    public static Color ToColor(this Tone tone) => tone switch
    {
        Tone.Info => Color.Info,
        Tone.Success => Color.Success,
        Tone.Warning => Color.Warning,
        Tone.Error => Color.Error,
        _ => Color.Default,
    };
}
