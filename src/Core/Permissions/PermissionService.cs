using CounterStrikeSharp.API.Core;
using CS2Ultimod.Core.Database;
using CS2Ultimod.Core.Utils;
using Microsoft.Extensions.Logging;

namespace CS2Ultimod.Core.Permissions;

public sealed class PermissionService : IPermissionService
{
    private readonly IDatabase _db;
    private readonly ILogger _logger;
    private record AdminEntry(HashSet<string> Flags, DateTimeOffset? ExpiresAt);
    private Dictionary<ulong, AdminEntry> _cache = [];

    public PermissionService(IDatabase db, ILogger logger)
    {
        _db = db;
        _logger = logger;
    }

    public bool HasFlag(CCSPlayerController player, string flag)
    {
        if (!player.IsValid || player.SteamID == 0) return false;
        if (!_cache.TryGetValue(player.SteamID, out var entry)) return false;
        if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value < DateTimeOffset.UtcNow) return false;
        return entry.Flags.Contains("@css/root") || entry.Flags.Contains(flag);
    }

    public bool RequireFlag(CCSPlayerController player, string flag)
    {
        if (HasFlag(player, flag)) return true;
        _logger.LogWarning("[Perms] DENY {Name} SteamID={SteamID} flag={Flag} | cache={Count} entries",
            player.PlayerName, player.SteamID, flag, _cache.Count);
        Chat.TellError(player, "Vous n'avez pas la permission d'exécuter cette commande.");
        return false;
    }

    public async Task<IReadOnlyList<string>> GetFlagsAsync(ulong steamId64)
    {
        var rows = await _db.QueryAsync<string>(
            "SELECT flag FROM admin_admins WHERE steam_id = @SteamId AND (expires_at IS NULL OR expires_at > datetime('now'))",
            new { SteamId = steamId64.ToString() });
        return rows;
    }

    public async Task SetFlagsAsync(ulong steamId64, IEnumerable<string> flags, DateTimeOffset? expiresAt = null)
    {
        var sid = steamId64.ToString();
        var expiresStr = expiresAt?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss");
        await _db.ExecuteAsync("DELETE FROM admin_admins WHERE steam_id = @SteamId", new { SteamId = sid });
        foreach (var flag in flags)
            await _db.ExecuteAsync(
                "INSERT INTO admin_admins (steam_id, flag, expires_at) VALUES (@SteamId, @Flag, @ExpiresAt)",
                new { SteamId = sid, Flag = flag, ExpiresAt = expiresStr });
        await ReloadAsync();
    }

    public async Task RemoveAsync(ulong steamId64)
    {
        await _db.ExecuteAsync("DELETE FROM admin_admins WHERE steam_id = @SteamId",
            new { SteamId = steamId64.ToString() });
        await ReloadAsync();
    }

    public async Task<IReadOnlyList<AdminSummary>> GetAllAdminsAsync()
    {
        var rows = (await _db.QueryAsync<AdminRow>(
            "SELECT steam_id, flag, expires_at FROM admin_admins")).ToList();
        return rows
            .GroupBy(r => r.steam_id)
            .Select(g =>
            {
                ulong.TryParse(g.Key, out var sid);
                return new AdminSummary(sid, g.Select(r => r.flag).ToList());
            })
            .Where(a => a.SteamId != 0)
            .ToList();
    }

    public async Task ReloadAsync()
    {
        try
        {
            var rows = (await _db.QueryAsync<AdminRow>(
                "SELECT steam_id, flag, expires_at FROM admin_admins")).ToList();

            _logger.LogInformation("[Perms] ReloadAsync: {Count} row(s) in admin_admins", rows.Count);
            foreach (var row in rows)
                _logger.LogInformation("[Perms]   steam_id={SteamId} flag={Flag}", row.steam_id, row.flag);

            var cache = new Dictionary<ulong, AdminEntry>();
            foreach (var row in rows)
            {
                if (!ulong.TryParse(row.steam_id, out var sid)) continue;
                DateTimeOffset? exp = row.expires_at != null ? DateTimeOffset.Parse(row.expires_at) : null;
                if (!cache.TryGetValue(sid, out var entry))
                    cache[sid] = entry = new AdminEntry([], exp);
                entry.Flags.Add(row.flag);
            }
            _cache = cache;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Perms] ReloadAsync failed");
        }
    }

    private sealed record AdminRow(string steam_id, string flag, string? expires_at);
}
