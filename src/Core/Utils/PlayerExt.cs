using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2Ultimod.Core.Utils;

public static class PlayerExt
{
    public static IEnumerable<CCSPlayerController> AllConnected()
        => Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot
            && p.Connected == PlayerConnectedState.Connected);

    public static IEnumerable<CCSPlayerController> AllAlive()
        => AllConnected().Where(p => p.PawnIsAlive);

    public static IEnumerable<CCSPlayerController> InTeam(CsTeam team)
        => AllConnected().Where(p => p.Team == team);

    public static CCSPlayerController? FindByName(string partial)
        => AllConnected().FirstOrDefault(p =>
            p.PlayerName.Contains(partial, StringComparison.OrdinalIgnoreCase));

    public static CCSPlayerController? FindBySteamId(ulong steamId64)
        => AllConnected().FirstOrDefault(p => p.SteamID == steamId64);

    // Resolves multi-target tokens: @all, @t, @ct, @spec, @alive, @dead, @me, @!me, or partial name.
    public static IReadOnlyList<CCSPlayerController> Resolve(string token, CCSPlayerController? caller = null)
        => token.ToLower() switch
        {
            "@all" => AllConnected().ToList(),
            "@t" => InTeam(CsTeam.Terrorist).ToList(),
            "@ct" => InTeam(CsTeam.CounterTerrorist).ToList(),
            "@spec" => InTeam(CsTeam.Spectator).ToList(),
            "@alive" => AllAlive().ToList(),
            "@dead" => AllConnected().Where(p => !p.PawnIsAlive).ToList(),
            "@me" when caller != null => [caller],
            "@!me" when caller != null => AllConnected().Where(p => p.Slot != caller.Slot).ToList(),
            _ => AllConnected().Where(p => p.PlayerName.Contains(token, StringComparison.OrdinalIgnoreCase)).ToList()
        };
}
