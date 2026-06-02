using CounterStrikeSharp.API.Core;

namespace CS2Ultimod.Core.Permissions;

public interface IPermissionService
{
    bool HasFlag(CCSPlayerController player, string flag);

    // Returns false and prints FR error message if flag missing.
    bool RequireFlag(CCSPlayerController player, string flag);

    Task<IReadOnlyList<string>> GetFlagsAsync(ulong steamId64);
    Task SetFlagsAsync(ulong steamId64, IEnumerable<string> flags, DateTimeOffset? expiresAt = null);
    Task RemoveAsync(ulong steamId64);
    Task ReloadAsync();

    // Liste tous les admins enregistrés (un par SteamID, flags regroupés).
    Task<IReadOnlyList<AdminSummary>> GetAllAdminsAsync();
}

public sealed record AdminSummary(ulong SteamId, IReadOnlyList<string> Flags);
