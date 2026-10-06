using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Sources.Navidrome;

/// <summary>
/// Performs interactive Navidrome sign-in from the Settings screen, returning a settings payload the
/// caller persists into a <see cref="SourceConfig"/>.
/// </summary>
public sealed class NavidromeAuthenticator
{
    public Task<NavidromeSourceSettings> AuthenticateAsync(
        string serverUrl, string username, string password, CancellationToken ct = default)
        => NavidromeClient.AuthenticateAsync(serverUrl, username, password, ct);
}
