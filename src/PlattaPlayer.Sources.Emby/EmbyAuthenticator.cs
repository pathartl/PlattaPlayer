using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Sources.Emby;

/// <summary>
/// Performs interactive Emby login from the Settings screen, returning a settings payload the
/// caller persists into a <see cref="SourceConfig"/>.
/// </summary>
public sealed class EmbyAuthenticator
{
    public Task<EmbySourceSettings> AuthenticateAsync(
        string serverUrl, string username, string password, CancellationToken ct = default)
        => EmbyClient.AuthenticateAsync(serverUrl, username, password, ct);
}
