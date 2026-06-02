using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2Ultimod.Core;
using CS2Ultimod.Core.Database;

namespace CS2Ultimod.Modes.Pickup;

/// <summary>
/// Represents a row in pickup_faceit_links.
/// </summary>
public sealed class FaceitLink
{
    public string SteamId { get; set; } = string.Empty;
    public string FaceitName { get; set; } = string.Empty;
    public string? FaceitId { get; set; }
    public int Elo { get; set; }
    public string UpdatedAt { get; set; } = string.Empty;
}

/// <summary>
/// Response model from Faceit open data API.
/// </summary>
internal sealed class FaceitPlayerResponse
{
    [JsonPropertyName("player_id")]
    public string? PlayerId { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    [JsonPropertyName("games")]
    public FaceitGames? Games { get; set; }
}

internal sealed class FaceitGames
{
    [JsonPropertyName("cs2")]
    public FaceitGameInfo? Cs2 { get; set; }

    [JsonPropertyName("csgo")]
    public FaceitGameInfo? Csgo { get; set; }
}

internal sealed class FaceitGameInfo
{
    [JsonPropertyName("faceit_elo")]
    public int FaceitElo { get; set; }
}

/// <summary>
/// Wraps the Faceit open-data API and handles caching via the database.
/// </summary>
public sealed class FaceitClient : IDisposable
{
    private readonly IDatabase _db;
    private readonly HttpClient _http;
    private const string BaseUrl = "https://open.faceit.com/data/v4/players";
    // Cache validity: 24 hours
    private static readonly TimeSpan CacheValidity = TimeSpan.FromHours(24);

    public FaceitClient(IDatabase db, string apiKey)
    {
        _db = db;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <summary>
    /// Fetches (or returns cached) Faceit data for the given username.
    /// Returns null if the account was not found or the API key is missing.
    /// Throws <see cref="FaceitApiException"/> on API/network errors.
    /// </summary>
    public async Task<FaceitLink?> FetchAndCacheAsync(string username, string steamId)
    {
        // 1. Check cache
        var cached = await _db.QuerySingleAsync<FaceitLink>(
            "SELECT steam_id AS SteamId, faceit_name AS FaceitName, faceit_id AS FaceitId, elo AS Elo, updated_at AS UpdatedAt " +
            "FROM pickup_faceit_links WHERE steam_id = @SteamId",
            new { SteamId = steamId });

        if (cached != null)
        {
            if (DateTime.TryParse(cached.UpdatedAt, out var updatedAt)
                && DateTime.UtcNow - updatedAt < CacheValidity)
            {
                return cached;
            }
        }

        // 2. Query API
        if (!_http.DefaultRequestHeaders.Contains("Authorization"))
            throw new FaceitApiException("Clé API Faceit non configurée.");

        FaceitPlayerResponse? response;
        try
        {
            using var resp = await _http.GetAsync($"{BaseUrl}?nickname={Uri.EscapeDataString(username)}");

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null; // player not found

            resp.EnsureSuccessStatusCode();

            response = await resp.Content.ReadFromJsonAsync<FaceitPlayerResponse>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (FaceitApiException) { throw; }
        catch (Exception ex)
        {
            throw new FaceitApiException($"Erreur réseau Faceit: {ex.Message}", ex);
        }

        if (response == null) return null;

        var elo = response.Games?.Cs2?.FaceitElo
               ?? response.Games?.Csgo?.FaceitElo
               ?? 0;

        // 3. Upsert into DB
        await _db.ExecuteAsync("""
            INSERT INTO pickup_faceit_links (steam_id, faceit_name, faceit_id, elo, updated_at)
            VALUES (@SteamId, @FaceitName, @FaceitId, @Elo, datetime('now'))
            ON CONFLICT(steam_id) DO UPDATE SET
                faceit_name = excluded.faceit_name,
                faceit_id   = excluded.faceit_id,
                elo         = excluded.elo,
                updated_at  = excluded.updated_at
            """,
            new
            {
                SteamId    = steamId,
                FaceitName = response.Nickname ?? username,
                FaceitId   = response.PlayerId,
                Elo        = elo
            });

        return new FaceitLink
        {
            SteamId    = steamId,
            FaceitName = response.Nickname ?? username,
            FaceitId   = response.PlayerId,
            Elo        = elo,
            UpdatedAt  = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
        };
    }

    /// <summary>
    /// Returns the cached Faceit link for a steam ID (null if not linked).
    /// </summary>
    public async Task<FaceitLink?> GetCachedAsync(string steamId)
        => await _db.QuerySingleAsync<FaceitLink>(
            "SELECT steam_id AS SteamId, faceit_name AS FaceitName, faceit_id AS FaceitId, elo AS Elo, updated_at AS UpdatedAt " +
            "FROM pickup_faceit_links WHERE steam_id = @SteamId",
            new { SteamId = steamId });

    public void Dispose() => _http.Dispose();
}

public sealed class FaceitApiException : Exception
{
    public FaceitApiException(string message) : base(message) { }
    public FaceitApiException(string message, Exception inner) : base(message, inner) { }
}
