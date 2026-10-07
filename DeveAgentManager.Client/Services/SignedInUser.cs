using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Events;

namespace DeveAgentManager.Client.Services;

/// <summary>
/// Who is signed in in this browser tab, and their roles: what the Portal shows and offers. Asked once (shared by every
/// page) and asked again when a user manager changed this user, or after the live connection was lost.
/// </summary>
public sealed class SignedInUser
{
    private readonly ManagerApi _api;
    private Task<CurrentUserDto>? _user;

    public SignedInUser(ManagerApi api, LiveUpdates live)
    {
        _api = api;
        live.Changed += change =>
        {
            if (change is { Kind: ManagerEventKind.UserChanged, UserId: { } id } && _user is { IsCompletedSuccessfully: true } current && current.Result.Id == id)
            {
                _ = RefreshAsync();
            }
        };
        live.Reconnected += () => _ = RefreshAsync();
    }

    /// <summary>Their roles changed: menus and pages show what they may do now.</summary>
    public event Action? Changed;

    public Task<CurrentUserDto> GetAsync() => _user is { IsFaulted: false } ? _user : _user = _api.MeAsync();

    private async Task RefreshAsync()
    {
        if (_user is null)
        {
            return;
        }

        try
        {
            _user = Task.FromResult(await _api.MeAsync());
            // The Manager's info depends on the roles too (provider settings for settings managers).
            await _api.InfoAsync(refresh: true);
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            // Kept as it was; asked again on the next change.
        }
    }
}
