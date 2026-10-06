using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Sources.Navidrome;

/// <summary>
/// A minimal client for the Subsonic REST API (and the servers that speak it: Navidrome, Airsonic, gonic, …) —
/// just ping, album paging and single-item lookups. Every call is a GET under <c>/rest</c> carrying the
/// credentials as query parameters; replies are JSON (<c>f=json</c>) wrapped in a <c>subsonic-response</c>
/// object whose <c>status</c> says whether the call worked (failures still come back as HTTP 200).
/// </summary>
internal sealed class NavidromeClient
{
    private const string ClientName = "PlattaPlayer";

    // Token auth needs API 1.13.0; servers accept any version up to their own.
    private const string ApiVersion = "1.16.1";

    // Subsonic error codes: wrong username or password, token auth unsupported for this user (e.g. LDAP), and
    // OpenSubsonic's "auth mechanism not supported".
    private const int WrongCredentials = 40;
    private const int TokenAuthUnsupported = 41;
    private const int AuthMechanismUnsupported = 42;
    private const int NotFound = 70;

    private const string NotSubsonic = "No Subsonic API found at that address.";

    // One HttpClient is shared across all Navidrome clients to avoid socket exhaustion.
    private static readonly HttpClient Http = new();

    // Some servers send numbers as strings and ids as numbers, so both are read leniently.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly string _serverUrl;
    private readonly string _authQuery;

    private NavidromeClient(NavidromeSourceSettings settings)
    {
        _serverUrl = settings.ServerUrl;
        var credentials = string.IsNullOrEmpty(settings.EncodedPassword)
            ? $"t={settings.Token}&s={settings.Salt}"
            : $"p={Uri.EscapeDataString(settings.EncodedPassword)}";
        _authQuery = $"u={Uri.EscapeDataString(settings.Username)}&{credentials}" +
                     $"&v={ApiVersion}&c={ClientName}&f=json";
    }

    /// <summary>Creates an authenticated client for an already-signed-in source.</summary>
    public static NavidromeClient Build(NavidromeSourceSettings settings) => new(settings);

    /// <summary>
    /// Checks the credentials against the server and returns a settings payload ready to persist. Token auth is
    /// tried first; servers that can't check a token (LDAP-backed users) fall back to a hex-encoded password.
    /// </summary>
    public static async Task<NavidromeSourceSettings> AuthenticateAsync(
        string serverUrl, string username, string password, CancellationToken ct = default)
    {
        password ??= string.Empty;
        var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var settings = new NavidromeSourceSettings
        {
            ServerUrl = NormalizeUrl(serverUrl),
            Username = username.Trim(),
            Salt = salt,
            Token = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(password + salt)))
        };

        var error = await Build(settings).PingAsync(ct);
        if (error?.Code is TokenAuthUnsupported or AuthMechanismUnsupported)
        {
            settings.Salt = settings.Token = string.Empty;
            settings.EncodedPassword = "enc:" + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(password));
            error = await Build(settings).PingAsync(ct);
        }

        if (error is not null)
            throw new InvalidOperationException(error.Code == WrongCredentials
                ? "Invalid username or password."
                : error.Message ?? $"The server refused the sign-in (error {error.Code}).");
        return settings;
    }

    /// <summary>One page of albums (ID3 tags), in album-artist order.</summary>
    public async Task<IReadOnlyList<Album>> GetAlbumsAsync(int offset, int size, CancellationToken ct)
        => (await GetAsync("getAlbumList2", $"type=alphabeticalByArtist&size={size}&offset={offset}", ct))
            ?.AlbumList2?.Album ?? [];

    /// <summary>An album with its songs, or null when the server no longer has it.</summary>
    public async Task<Album?> GetAlbumAsync(string albumId, CancellationToken ct)
        => (await GetAsync("getAlbum", $"id={Uri.EscapeDataString(albumId)}", ct, allowNotFound: true))?.Album;

    /// <summary>A song, or null when the server no longer has it.</summary>
    public async Task<Song?> GetSongAsync(string songId, CancellationToken ct)
        => (await GetAsync("getSong", $"id={Uri.EscapeDataString(songId)}", ct, allowNotFound: true))?.Song;

    /// <summary>A cover-art image, or null when the server has none.</summary>
    public async Task<byte[]?> GetCoverArtAsync(string coverArtId, CancellationToken ct)
    {
        using var response = await Http.GetAsync(Url("getCoverArt", $"id={Uri.EscapeDataString(coverArtId)}"), ct);
        // A missing image is reported as a JSON (or XML) error body rather than an HTTP error.
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not { } type ||
            !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return null;
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>The original file, untranscoded, authenticated by query string for the engine.</summary>
    public string StreamUrl(string songId)
        => Url("stream", $"id={Uri.EscapeDataString(songId)}&format=raw");

    private async Task<SubsonicError?> PingAsync(CancellationToken ct)
    {
        var body = await SendAsync("ping", string.Empty, ct);
        return body.Status == "ok" ? null : body.Error ?? new SubsonicError();
    }

    private async Task<ResponseBody?> GetAsync(
        string method, string query, CancellationToken ct, bool allowNotFound = false)
    {
        var body = await SendAsync(method, query, ct);
        if (body.Status == "ok")
            return body;
        if (allowNotFound && body.Error?.Code == NotFound)
            return null;
        throw new InvalidOperationException(body.Error?.Code is WrongCredentials
            ? "The server rejected the saved sign-in; remove the source and add it again."
            : body.Error?.Message ?? $"The server reported an error ({method}).");
    }

    private async Task<ResponseBody> SendAsync(string method, string query, CancellationToken ct)
    {
        using var response = await Http.GetAsync(Url(method, query), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(NotSubsonic);
        response.EnsureSuccessStatusCode();

        Envelope? envelope;
        try
        {
            envelope = await response.Content.ReadFromJsonAsync<Envelope>(Json, ct);
        }
        catch (JsonException)
        {
            envelope = null; // e.g. a web page from a wrong address
        }
        return envelope?.Response ?? throw new InvalidOperationException(NotSubsonic);
    }

    private string Url(string method, string query)
        => $"{_serverUrl}/rest/{method}?{_authQuery}" + (query.Length > 0 ? "&" + query : string.Empty);

    /// <summary>
    /// Trims the address to scheme + host (+ any base path), dropping a trailing <c>/rest</c>, or Navidrome's
    /// <c>/app</c> and any <c>#…</c> web-app route that users paste from the address bar, and assumes http when
    /// no scheme is given.
    /// </summary>
    private static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (url.IndexOf('#') is var hash and >= 0)
            url = url[..hash];
        url = url.TrimEnd('/');
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "http://" + url;
        foreach (var suffix in (string[])["/rest", "/app"])
            if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                url = url[..^suffix.Length];
        return url;
    }

    private sealed class Envelope
    {
        [JsonPropertyName("subsonic-response")]
        public ResponseBody? Response { get; set; }
    }

    internal sealed class ResponseBody
    {
        public string? Status { get; set; }
        public SubsonicError? Error { get; set; }
        public AlbumList? AlbumList2 { get; set; }
        public Album? Album { get; set; }
        public Song? Song { get; set; }
    }

    internal sealed class SubsonicError
    {
        public int Code { get; set; }
        public string? Message { get; set; }
    }

    internal sealed class AlbumList
    {
        public List<Album>? Album { get; set; }
    }

    internal sealed class Album
    {
        [JsonConverter(typeof(LenientStringConverter))] public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Artist { get; set; }
        public int? Year { get; set; }
        public string? Genre { get; set; }
        [JsonConverter(typeof(LenientStringConverter))] public string? CoverArt { get; set; }
        public List<Song>? Song { get; set; }

        // OpenSubsonic extensions.
        public List<NamedItem>? Artists { get; set; }
        public List<NamedItem>? Genres { get; set; }
    }

    internal sealed class Song
    {
        [JsonConverter(typeof(LenientStringConverter))] public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Album { get; set; }
        [JsonConverter(typeof(LenientStringConverter))] public string? AlbumId { get; set; }
        public string? Artist { get; set; }
        public int? Track { get; set; }
        public int? DiscNumber { get; set; }
        public int? Year { get; set; }
        public string? Genre { get; set; }
        public int? Duration { get; set; }
        public string? Suffix { get; set; }
        [JsonConverter(typeof(LenientStringConverter))] public string? CoverArt { get; set; }
        public bool IsDir { get; set; }
        public bool IsVideo { get; set; }

        // OpenSubsonic extensions.
        public List<NamedItem>? Artists { get; set; }
        public List<NamedItem>? AlbumArtists { get; set; }
        public List<NamedItem>? Genres { get; set; }
    }

    internal sealed class NamedItem
    {
        public string? Name { get; set; }
    }

    /// <summary>Reads an id that the server may send as a JSON string or a number.</summary>
    private sealed class LenientStringConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
                JsonTokenType.Null => null,
                _ => throw new JsonException($"Expected an id, got {reader.TokenType}.")
            };

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);
    }
}
