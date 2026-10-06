using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PlattaPlayer.Core.Models;
using Plex.ServerApi;
using Plex.ServerApi.Api;
using Plex.ServerApi.Clients;
using Plex.ServerApi.Clients.Interfaces;
using Plex.ServerApi.PlexModels.Account.Resources;

namespace PlattaPlayer.Sources.Plex;

/// <summary>
/// Builds Plex.Api clients from stored settings and performs the initial sign-in. Plex signs in against
/// plex.tv (password, or a PIN the user approves in the browser); the account's resource list then names
/// the media servers it can reach, each with its own access token and candidate connections.
/// </summary>
internal static class PlexClientFactory
{
    private const string ProductName = "PlattaPlayer";
    private const string ProductVersion = "1.0.0";
    private const string DeviceName = "PlattaPlayer Desktop";

    // How long to wait for one candidate server connection before giving up on it.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PinPollInterval = TimeSpan.FromSeconds(2);

    // One HttpClient (and one Plex.Api service over it) is shared across all Plex sources.
    internal static readonly HttpClient Http = new();
    private static readonly IApiService Api = new ApiService(new SharedHttpClient(), NullLogger<ApiService>.Instance);

    public static IPlexServerClient ServerClient(string deviceId) => new PlexServerClient(Options(deviceId), Api);

    public static IPlexLibraryClient LibraryClient(string deviceId) => new PlexLibraryClient(Options(deviceId), Api);

    private static IPlexAccountClient AccountClient(string deviceId) => new PlexAccountClient(Options(deviceId), Api);

    /// <summary>Signs in with a plex.tv username/email and password, then picks a server.</summary>
    public static async Task<PlexSourceSettings> AuthenticateAsync(
        string? serverUrl, string username, string password, CancellationToken ct = default)
    {
        var deviceId = NewDeviceId();
        global::Plex.ServerApi.Models.User? user;
        try
        {
            user = await AccountClient(deviceId).SignInAsync(username, password).WaitAsync(ct);
        }
        catch (ApplicationException)
        {
            // Plex.Api reports every non-success status the same way; a failed sign-in is a 401.
            throw new InvalidOperationException(
                "Sign-in failed. Check the username and password; with two-factor authentication on, " +
                "append the current code to the password.");
        }

        if (string.IsNullOrEmpty(user?.AuthenticationToken))
            throw new InvalidOperationException("Sign-in failed: plex.tv did not return an access token.");

        return await ConnectAsync(serverUrl, user.AuthenticationToken, user.Uuid ?? string.Empty,
            user.Username ?? username, deviceId, ct);
    }

    /// <summary>
    /// Signs in through the browser: creates a PIN, hands its approval page to <paramref name="openBrowser"/>,
    /// and polls plex.tv until the user approves it (or the PIN expires, or <paramref name="ct"/> fires).
    /// </summary>
    public static async Task<PlexSourceSettings> AuthenticateInBrowserAsync(
        string? serverUrl, Func<Uri, Task> openBrowser, CancellationToken ct = default)
    {
        var deviceId = NewDeviceId();
        var account = AccountClient(deviceId);
        var pin = await account.CreateOAuthPinAsync(string.Empty).WaitAsync(ct);
        await openBrowser(new Uri(pin.Url));

        var expires = DateTime.UtcNow.AddSeconds(pin.ExpiresIn > 0 ? pin.ExpiresIn : 900);
        string? token = null;
        while (token is null)
        {
            if (DateTime.UtcNow > expires)
                throw new InvalidOperationException("The sign-in request expired. Try again.");
            await Task.Delay(PinPollInterval, ct);
            var polled = await account.GetAuthTokenFromOAuthPinAsync(pin.Id.ToString()).WaitAsync(ct);
            if (!string.IsNullOrEmpty(polled?.AuthToken))
                token = polled.AuthToken;
        }

        // The PIN carries no user details; they are only for display, so a failed lookup is not fatal.
        var (userId, username) = await TryGetAccountAsync(account, token, ct);
        return await ConnectAsync(serverUrl, token, userId, username, deviceId, ct);
    }

    /// <summary>
    /// Picks the server and connection for a signed-in account: the one at <paramref name="serverUrl"/> when
    /// given, else the first (owned servers first) that answers and has a music library.
    /// </summary>
    private static async Task<PlexSourceSettings> ConnectAsync(
        string? serverUrl, string accountToken, string userId, string username, string deviceId, CancellationToken ct)
    {
        var resources = await AccountClient(deviceId).GetResourcesAsync(accountToken).WaitAsync(ct) ?? [];
        var servers = resources
            .Where(r => r.Provides?.Split(',').Contains("server") == true)
            .OrderByDescending(r => r.Owned)
            .ToList();

        if (!string.IsNullOrWhiteSpace(serverUrl))
        {
            var url = NormalizeUrl(serverUrl);
            // A shared server only accepts its own token, so find which resource the URL points at.
            var match = servers.FirstOrDefault(s => s.Connections?.Any(c => SameEndpoint(c, url)) == true);
            var token = match?.AccessToken ?? accountToken;
            if (await ProbeAsync(url, token, ct) is not { } hasMusic)
                throw new InvalidOperationException($"Could not reach a Plex server at {url} with this account.");
            if (!hasMusic)
                throw new InvalidOperationException("That Plex server has no music library.");
            return Settings(url, token, userId, username, deviceId);
        }

        if (servers.Count == 0)
            throw new InvalidOperationException("This Plex account has no servers.");

        var reachedAny = false;
        foreach (var server in servers)
        {
            var token = server.AccessToken ?? accountToken;
            var (url, hasMusic) = await FindConnectionAsync(server, token, ct);
            if (url is null) continue;
            reachedAny = true;
            if (hasMusic)
                return Settings(url, token, userId, username, deviceId);
        }

        throw new InvalidOperationException(reachedAny
            ? "None of this account's Plex servers has a music library."
            : "Could not reach any of this account's Plex servers. Enter the server URL to connect directly.");
    }

    /// <summary>
    /// Probes a server's connections at once and returns the most direct one that answered: local before
    /// remote, relayed last (Plex's relay is bandwidth-capped).
    /// </summary>
    private static async Task<(string? Url, bool HasMusic)> FindConnectionAsync(
        Resource server, string token, CancellationToken ct)
    {
        var candidates = (server.Connections ?? [])
            .Where(c => !string.IsNullOrEmpty(c.Uri))
            .OrderBy(c => c.Relay)
            .ThenByDescending(c => c.Local)
            .ToList();
        var results = await Task.WhenAll(candidates.Select(c => ProbeAsync(NormalizeUrl(c.Uri), token, ct)));

        for (var i = 0; i < candidates.Count; i++)
            if (results[i] is { } hasMusic)
                return (NormalizeUrl(candidates[i].Uri), hasMusic);
        return (null, false);
    }

    /// <summary>Whether the server answers with this token: null if not, else whether it has a music library.</summary>
    private static async Task<bool?> ProbeAsync(string url, string token, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{url}/library/sections");
            request.Headers.Add("X-Plex-Token", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await Http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return null;
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
            return json.RootElement.TryGetProperty("MediaContainer", out var container)
                   && container.TryGetProperty("Directory", out var sections)
                   && sections.EnumerateArray().Any(IsMusicSection);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static bool IsMusicSection(JsonElement section)
        => section.TryGetProperty("type", out var type) && type.GetString() == PlexMediaSource.MusicSectionType;

    private static async Task<(string UserId, string Username)> TryGetAccountAsync(
        IPlexAccountClient account, string token, CancellationToken ct)
    {
        try
        {
            var info = await account.GetPlexAccountAsync(token).WaitAsync(ct);
            return (info?.Uuid ?? string.Empty, info?.Username ?? info?.Title ?? string.Empty);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return (string.Empty, string.Empty);
        }
    }

    private static PlexSourceSettings Settings(
        string url, string token, string userId, string username, string deviceId) => new()
    {
        ServerUrl = url,
        AccessToken = token,
        UserId = userId,
        Username = username,
        DeviceId = deviceId
    };

    /// <summary>Whether a resource connection is the server the user typed (by host and port, so http/https and plex.direct names don't matter when the address does).</summary>
    private static bool SameEndpoint(ResourceConnection connection, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var typed))
            return false;
        if (Uri.TryCreate(connection.Uri, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, typed.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == typed.Port)
            return true;
        return string.Equals(connection.Address, typed.Host, StringComparison.OrdinalIgnoreCase)
               && connection.Port == typed.Port;
    }

    private static ClientOptions Options(string deviceId) => new()
    {
        Product = ProductName,
        Version = ProductVersion,
        DeviceName = DeviceName,
        ClientId = deviceId,
        Platform = "Windows"
    };

    private static string NewDeviceId() => Guid.NewGuid().ToString("N");

    internal static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');

    /// <summary>Routes Plex.Api's requests through the shared <see cref="Http"/> client.</summary>
    private sealed class SharedHttpClient : IPlexRequestsHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => Http.SendAsync(request);
    }
}
