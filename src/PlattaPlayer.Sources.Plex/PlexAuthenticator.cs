using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Sources.Plex;

/// <summary>
/// Performs interactive Plex sign-in from the Settings screen, returning a settings payload the caller
/// persists into a <see cref="SourceConfig"/>. The server URL is optional: without it the account's own
/// servers are discovered through plex.tv.
/// </summary>
public sealed class PlexAuthenticator
{
    public Task<PlexSourceSettings> AuthenticateAsync(
        string? serverUrl, string username, string password, CancellationToken ct = default)
        => PlexClientFactory.AuthenticateAsync(serverUrl, username, password, ct);

    /// <summary>Signs in by approving a PIN on plex.tv; <paramref name="openBrowser"/> shows the approval page.</summary>
    public Task<PlexSourceSettings> AuthenticateInBrowserAsync(
        string? serverUrl, Func<Uri, Task> openBrowser, CancellationToken ct = default)
        => PlexClientFactory.AuthenticateInBrowserAsync(serverUrl, openBrowser, ct);
}
