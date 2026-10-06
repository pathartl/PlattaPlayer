using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Sources.Emby;

/// <summary>
/// A minimal client for the Emby Server REST API — just login, item paging and single-item lookups. Emby has
/// no maintained .NET SDK, and the few endpoints needed are plain JSON over HTTP. Every request goes under the
/// <c>/emby</c> path prefix, which Emby always serves (reverse proxies commonly expose only that).
/// </summary>
internal sealed class EmbyClient
{
    private const string ClientName = "PlattaPlayer";
    private const string ClientVersion = "1.0.0";
    private const string DeviceName = "PlattaPlayer Desktop";

    // One HttpClient is shared across all Emby clients to avoid socket exhaustion.
    private static readonly HttpClient Http = new();

    // Responses are PascalCase; Web defaults read them case-insensitively. Request bodies keep PascalCase.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions RequestJson = new();

    private readonly string _serverUrl;
    private readonly string _deviceId;
    private readonly string? _userId;
    private readonly string? _accessToken;

    private EmbyClient(string serverUrl, string deviceId, string? userId, string? accessToken)
    {
        _serverUrl = serverUrl;
        _deviceId = deviceId;
        _userId = userId;
        _accessToken = accessToken;
    }

    /// <summary>Creates an authenticated client for an already-logged-in source.</summary>
    public static EmbyClient Build(EmbySourceSettings settings)
        => new(settings.ServerUrl, settings.DeviceId, settings.UserId, settings.AccessToken);

    /// <summary>
    /// Authenticates against a server with a username/password and returns a fully populated settings
    /// payload (access token, user id, device id) ready to persist.
    /// </summary>
    public static async Task<EmbySourceSettings> AuthenticateAsync(
        string serverUrl, string username, string password, CancellationToken ct = default)
    {
        var normalizedUrl = NormalizeUrl(serverUrl);
        var deviceId = Guid.NewGuid().ToString("N");
        var client = new EmbyClient(normalizedUrl, deviceId, null, null);

        using var request = client.CreateRequest(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Content = JsonContent.Create(new { Username = username, Pw = password ?? string.Empty }, options: RequestJson);
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Invalid username or password.");
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AuthenticationResult>(Json, ct);
        if (string.IsNullOrEmpty(result?.AccessToken) || string.IsNullOrEmpty(result.User?.Id))
            throw new InvalidOperationException("Authentication failed: the server did not return an access token.");

        return new EmbySourceSettings
        {
            ServerUrl = normalizedUrl,
            UserId = result.User.Id,
            AccessToken = result.AccessToken,
            Username = result.User.Name ?? username,
            DeviceId = deviceId
        };
    }

    /// <summary>One page of the user's audio items, in album-artist / album / track order.</summary>
    public Task<ItemsResult?> GetAudioItemsAsync(int startIndex, int limit, CancellationToken ct)
        => GetAsync<ItemsResult>(
            $"/Users/{_userId}/Items?Recursive=true&IncludeItemTypes=Audio" +
            "&SortBy=AlbumArtist,Album,ParentIndexNumber,IndexNumber&SortOrder=Ascending" +
            $"&Fields=Genres,ProductionYear&StartIndex={startIndex}&Limit={limit}", ct);

    public Task<BaseItem?> GetItemAsync(string itemId, CancellationToken ct)
        => GetAsync<BaseItem>($"/Users/{_userId}/Items/{Uri.EscapeDataString(itemId)}", ct);

    /// <summary>An item's primary image, or null when it has none.</summary>
    public async Task<byte[]?> GetPrimaryImageAsync(string itemId, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, $"/Items/{Uri.EscapeDataString(itemId)}/Images/Primary");
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Direct (static) stream of the original file, authenticated by query string for the engine.</summary>
    public string StreamUrl(string itemId)
        => $"{_serverUrl}/emby/Audio/{Uri.EscapeDataString(itemId)}/stream" +
           $"?static=true&api_key={Uri.EscapeDataString(_accessToken ?? string.Empty)}";

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Emby rejected the saved sign-in; remove the source and add it again.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{_serverUrl}/emby{path}");
        request.Headers.TryAddWithoutValidation("X-Emby-Authorization",
            $"Emby UserId=\"{_userId}\", Client=\"{ClientName}\", Device=\"{DeviceName}\", " +
            $"DeviceId=\"{_deviceId}\", Version=\"{ClientVersion}\"");
        if (!string.IsNullOrEmpty(_accessToken))
            request.Headers.TryAddWithoutValidation("X-Emby-Token", _accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    /// <summary>
    /// Trims the address to scheme + host (+ any base path), dropping a trailing <c>/emby</c> that users often
    /// paste from the web app's address bar, and assumes http when no scheme is given.
    /// </summary>
    private static string NormalizeUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "http://" + url;
        if (url.EndsWith("/emby", StringComparison.OrdinalIgnoreCase))
            url = url[..^"/emby".Length];
        return url;
    }

    internal sealed class AuthenticationResult
    {
        public UserDto? User { get; set; }
        public string? AccessToken { get; set; }
    }

    internal sealed class UserDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    internal sealed class ItemsResult
    {
        public List<BaseItem>? Items { get; set; }
        public int TotalRecordCount { get; set; }
    }

    internal sealed class BaseItem
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Album { get; set; }
        public string? AlbumId { get; set; }
        public string? AlbumArtist { get; set; }
        public List<NameIdPair>? AlbumArtists { get; set; }
        public List<string>? Artists { get; set; }
        public List<string>? Genres { get; set; }
        public int? IndexNumber { get; set; }
        public int? ParentIndexNumber { get; set; }
        public int? ProductionYear { get; set; }
        public long? RunTimeTicks { get; set; }
        public string? Container { get; set; }
        public Dictionary<string, string>? ImageTags { get; set; }
        public string? AlbumPrimaryImageTag { get; set; }

        public bool HasPrimaryImage => ImageTags?.ContainsKey("Primary") == true;
    }

    internal sealed class NameIdPair
    {
        public string? Name { get; set; }
        public string? Id { get; set; }
    }
}
