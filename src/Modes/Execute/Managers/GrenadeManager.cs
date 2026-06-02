using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Modes.Execute.Models;
using Microsoft.Extensions.Logging;

namespace CS2Ultimod.Modes.Execute.Managers;

public sealed class GrenadeManager
{
    private readonly List<CounterStrikeSharp.API.Modules.Timers.Timer> _activeTimers = new();

    /// <summary>
    /// Schedules grenade throws for the given scenario.
    /// One grenade per player per team maximum (matching bazookaCodes behaviour).
    /// </summary>
    public void SetupGrenades(Scenario scenario)
    {
        CancelAll();

        var totalConfigured = (scenario.Grenades.TryGetValue(CsTeam.Terrorist, out var tNades) ? tNades.Count : 0)
                            + (scenario.Grenades.TryGetValue(CsTeam.CounterTerrorist, out var ctNades) ? ctNades.Count : 0);
        if (totalConfigured == 0)
            CS2Ultimod.CS2UltimodPlugin.Log?.LogWarning(
                "[Execute] Scenario '{Name}' has no grenades configured for either team.", scenario.Name);

        // Get freeze-time duration so grenades launch at round start, not during freeze.
        int freezeTime = 0;
        try
        {
            var cv = ConVar.Find("mp_freezetime");
            if (cv != null)
                freezeTime = cv.GetPrimitiveValue<int>();
        }
        catch { /* ConVar may not exist in some server configs */ }

        var teams = new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist };

        foreach (var team in teams)
        {
            if (!scenario.Grenades.TryGetValue(team, out var grenades)) continue;

            int playerCount = CounterStrikeSharp.API.Utilities.GetPlayers()
                .Count(p => p.IsValid && p.Team == team);

            int thrown = 0;
            foreach (var grenade in grenades)
            {
                if (thrown >= playerCount) break;

                var g    = grenade; // capture
                var delay = (float)(freezeTime + g.Delay);
                var t = new CounterStrikeSharp.API.Modules.Timers.Timer(
                    delay,
                    g.Throw,
                    TimerFlags.STOP_ON_MAPCHANGE);
                _activeTimers.Add(t);
                thrown++;
            }
        }
    }

    public void CancelAll()
    {
        foreach (var t in _activeTimers)
        {
            try { t.Kill(); } catch { /* timer may have already fired */ }
        }
        _activeTimers.Clear();
    }
}
