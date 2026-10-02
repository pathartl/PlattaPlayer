using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Sources.Jellyfin;

/// <summary>
/// Builds <see cref="JellyfinApiClient"/> instances from stored settings and performs the initial
/// username/password login. The Jellyfin SDK is a Kiota-generated client: it needs a settings object
/// (client identity + server URL + token), an auth provider, and a request adapter over an HttpClient.
/// </summary>
internal static class JellyfinClientFactory
{
    private const string ClientName = "PlattaPlayer";
    private const string ClientVersion = "1.0.0";
    private const string DeviceName = "PlattaPlayer Desktop";

    // One HttpClient is shared across all Jellyfin clients to avoid socket exhaustion.
    private static readonly HttpClient Http = new();

    /// <summary>Creates an authenticated client for an already-logged-in source.</summary>
    public static JellyfinApiClient Build(JellyfinSourceSettings settings)
    {
        var sdkSettings = CreateSettings(settings.ServerUrl, settings.DeviceId);
        sdkSettings.SetAccessToken(settings.AccessToken);
        return BuildClient(sdkSettings);
    }

    /// <summary>
    /// Authenticates against a server with a username/password and returns a fully populated
    /// settings payload (access token, user id, device id) ready to persist.
    /// </summary>
    public static async Task<JellyfinSourceSettings> AuthenticateAsync(
        string serverUrl, string username, string password, CancellationToken ct = default)
    {
        var normalizedUrl = NormalizeUrl(serverUrl);
        var deviceId = Guid.NewGuid().ToString("N");

        var sdkSettings = CreateSettings(normalizedUrl, deviceId);
        var client = BuildClient(sdkSettings);

        var result = await client.Users.AuthenticateByName.PostAsync(
            new AuthenticateUserByName { Username = username, Pw = password }, cancellationToken: ct);

        if (result?.AccessToken is null || result.User?.Id is null)
            throw new InvalidOperationException("Authentication failed: the server did not return an access token.");

        return new JellyfinSourceSettings
        {
            ServerUrl = normalizedUrl,
            UserId = result.User.Id.Value.ToString("N"),
            AccessToken = result.AccessToken,
            Username = result.User.Name ?? username,
            DeviceId = deviceId
        };
    }

    private static JellyfinSdkSettings CreateSettings(string serverUrl, string deviceId)
    {
        var settings = new JellyfinSdkSettings();
        settings.Initialize(ClientName, ClientVersion, DeviceName, deviceId);
        settings.SetServerUrl(NormalizeUrl(serverUrl));
        return settings;
    }

    private static JellyfinApiClient BuildClient(JellyfinSdkSettings sdkSettings)
    {
        var authProvider = new JellyfinAuthenticationProvider(sdkSettings);
        var adapter = new JellyfinRequestAdapter(authProvider, sdkSettings, Http);
        return new JellyfinApiClient(adapter);
    }

    private static string NormalizeUrl(string url) => url.TrimEnd('/');
}
