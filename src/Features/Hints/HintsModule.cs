using CounterStrikeSharp.API.Modules.Timers;
using CS2Ultimod.Core;
using CS2Ultimod.Core.Utils;

namespace CS2Ultimod.Features.Hints;

// Diffuse en rotation des rappels de commandes utiles dans le chat.
// Une seule ligne par tick pour éviter le mur de texte ; filtré par mode
// pour ne pas suggérer une commande inactive (ex: !guns en Stuff).
public static class HintsModule
{
    private const float IntervalSeconds = 180f;

    private sealed record Hint(string Message, GameMode[] Modes);

    private static readonly Hint[] _hints =
    [
        new("\x04!guns\x01 — ouvre le menu de choix d'armes (full-buy)",
            [GameMode.Retake, GameMode.Execute, GameMode.Mixte]),
        new("Raccourcis armes : \x04!ak !m4 !awp !deagle !aug !sg !usp !cz !p250 !tec\x01",
            [GameMode.Retake, GameMode.Execute, GameMode.Mixte]),
        new("Votes : \x04!votemap\x01 (map), \x04!votemode\x01 (mode), \x04!votesh\x01 (superheroes)",
            [GameMode.Retake, GameMode.Execute, GameMode.Mixte, GameMode.Stuff, GameMode.Pickup]),
        new("Superheroes : un pouvoir aléatoire par round selon ton score. \x04!sh\x01 pour voir le tien, \x04!votesh\x01 pour configurer",
            [GameMode.Retake, GameMode.Execute, GameMode.Mixte, GameMode.Pickup]),
    ];

    private static int _cursor;
    private static CounterStrikeSharp.API.Modules.Timers.Timer? _timer;

    public static void Register()
    {
        _timer = new CounterStrikeSharp.API.Modules.Timers.Timer(IntervalSeconds, Tick, TimerFlags.REPEAT);
    }

    private static void Tick()
    {
        var current = CS2UltimodPlugin.ModeManager.Current;

        // Cherche le prochain hint applicable au mode courant, en avançant le curseur.
        // Borne la recherche à _hints.Length pour éviter une boucle infinie si aucun
        // hint ne matche (peu probable mais safe).
        for (int tried = 0; tried < _hints.Length; tried++)
        {
            var hint = _hints[_cursor];
            _cursor = (_cursor + 1) % _hints.Length;
            if (Array.IndexOf(hint.Modes, current) >= 0)
            {
                Chat.Broadcast(hint.Message);
                return;
            }
        }
    }
}
