using DeveAgentManager.Core.Events;
using Microsoft.AspNetCore.Components;

namespace DeveAgentManager.Components.Shared;

/// <summary>
/// A component that loads its data in <see cref="LoadAsync"/> and reloads it (debounced) whenever the Manager reports a change,
/// so pages update live while Runners are created or agents start.
/// </summary>
public abstract class LiveComponent : ComponentBase, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private int _reloadPending;
    private bool _disposed;

    [Inject]
    protected ManagerEvents Events { get; set; } = default!;

    protected bool Loaded { get; private set; }

    protected abstract Task LoadAsync();

    /// <summary>Return false to ignore events that don't concern this component.</summary>
    protected virtual bool IsRelevant(ManagerEvent managerEvent) => true;

    protected override async Task OnInitializedAsync()
    {
        Events.Changed += OnManagerEvent;
        await LoadAsync();
        Loaded = true;
    }

    protected Task ReloadAsync() => InvokeAsync(async () =>
    {
        await LoadAsync();
        StateHasChanged();
    });

    private void OnManagerEvent(ManagerEvent managerEvent)
    {
        if (_disposed || !IsRelevant(managerEvent) || Interlocked.Exchange(ref _reloadPending, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(Debounce);
            Interlocked.Exchange(ref _reloadPending, 0);
            if (!_disposed)
            {
                try
                {
                    await ReloadAsync();
                }
                catch (ObjectDisposedException)
                {
                    // The circuit went away while reloading.
                }
            }
        });
    }

    public virtual void Dispose()
    {
        _disposed = true;
        Events.Changed -= OnManagerEvent;
        GC.SuppressFinalize(this);
    }
}
