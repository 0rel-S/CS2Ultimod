using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CS2Ultimod.Core;
using CS2Ultimod.Core.Utils;
using CS2Ultimod.Features.Superheroes;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CS2Ultimod.Features.Votes;

public static class VoteModule
{
    private static VoteSession? _activeVote;
    // Cooldown séparé par type de vote : enchaîner map + mode + sh est légitime,
    // seul le spam du MÊME vote doit être bloqué.
    private static readonly Dictionary<string, DateTimeOffset> _lastVoteEnd = new();
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(120);
    private const int DurationSec = 30;

    private static bool CheckAvailable(CCSPlayerController player, string kind)
    {
        if (_activeVote != null)
        {
            Chat.TellError(player, "Un vote est déjà en cours.");
            return false;
        }
        if (_lastVoteEnd.TryGetValue(kind, out var last) && DateTimeOffset.UtcNow - last < Cooldown)
        {
            var remaining = (int)(Cooldown - (DateTimeOffset.UtcNow - last)).TotalSeconds;
            Chat.TellError(player, $"!{kind} non disponible, patientez encore {remaining}s.");
            return false;
        }
        return true;
    }

    private static MapPoolConfig? _pools;

    public static void Register()
    {
        _pools = LoadPools();

        CS2UltimodPlugin.Commands.Register(new ChatCommand(
            "votemap", null, null, "",
            OnVoteMap));

        CS2UltimodPlugin.Commands.Register(new ChatCommand(
            "votemode", null, null, "",
            OnVoteMode));

        CS2UltimodPlugin.Commands.Register(new ChatCommand(
            "votesh", null, null, "",
            OnVoteSh));

        // Bulletin de vote : chaque joueur tape !vote <numéro> dans le chat.
        // Pas de menu (qui figerait tout le monde) — le vote vit dans le chat.
        CS2UltimodPlugin.Commands.Register(new ChatCommand(
            "vote", null, null, "<numéro> — voter pour l'option en cours",
            OnVoteCast));
    }

    public static IReadOnlyList<string> GetMapPool(GameMode mode) =>
        mode switch
        {
            GameMode.Arena  => _pools?.Arena ?? [],
            GameMode.Pickup => _pools?.Pickup ?? [],
            GameMode.Stuff  => _pools?.Stuff ?? [],
            _ => _pools?.RetakeExecuteMixte ?? [],
        };

    // Une entrée de pool est soit une map officielle ("de_mirage" → "map de_mirage"),
    // soit une map Workshop au format "Nom lisible=workshopId" → "host_workshop_map id".
    // Renvoie le libellé affiché dans le vote et la commande de changement à exécuter.
    private static (string Display, string Command) ParseMapEntry(string entry)
    {
        var idx = entry.IndexOf('=');
        if (idx > 0 && idx < entry.Length - 1)
        {
            var display = entry[..idx].Trim();
            var id = entry[(idx + 1)..].Trim();
            return (display, $"host_workshop_map {id}");
        }
        return (entry, $"map {entry}");
    }

    private static void OnVoteMap(CCSPlayerController player, string[] args)
    {
        if (!CheckAvailable(player, "votemap")) return;

        var pool = GetMapPool(CS2UltimodPlugin.ModeManager.Current);
        if (pool.Count == 0)
        {
            Chat.TellError(player, "Aucune map disponible dans le pool.");
            return;
        }

        // Libellés affichés + commande de changement par libellé (gère Workshop).
        var entries = pool.Select(ParseMapEntry).ToList();
        var commandByDisplay = new Dictionary<string, string>();
        foreach (var (display, command) in entries)
            commandByDisplay[display] = command;

        StartVote("votemap", $"Vote map ({CS2UltimodPlugin.ModeManager.Current})", commandByDisplay.Keys.ToList(),
            winner =>
            {
                Chat.Broadcast($"Vote terminé ! Map suivante : {winner}");
                Server.ExecuteCommand(commandByDisplay.TryGetValue(winner, out var cmd) ? cmd : $"map {winner}");
            });
    }

    public static void TriggerVoteMode(CCSPlayerController player, string[] args)
        => OnVoteMode(player, args);

    private static void OnVoteMode(CCSPlayerController player, string[] args)
    {
        if (!CheckAvailable(player, "votemode")) return;

        var modes = Enum.GetNames<GameMode>();
        StartVote("votemode", "Vote mode de jeu", modes,
            winner =>
            {
                if (Enum.TryParse<GameMode>(winner, out var mode))
                {
                    Chat.Broadcast($"Vote terminé ! Mode suivant : {mode}");
                    _ = CS2UltimodPlugin.ModeManager.SwitchToAsync(mode, reason: "vote joueurs");
                }
            });
    }

    // Vote 4 options : off + 3 modes. Libellés contextuels :
    //  - "Désactiver" si SH actif, "Garder désactivé" si déjà off (le verbe seul est
    //    ambigu dans l'état off).
    //  - "✓ " préfixe l'option qui correspond à l'état courant.
    // Mapping label→sub via dico pour absorber le préfixe sans dupliquer le switch.
    private static void OnVoteSh(CCSPlayerController player, string[] args)
    {
        if (!CheckAvailable(player, "votesh")) return;

        var enabled = SuperheroesModule.IsEnabled;
        var current = SuperheroesModule.CurrentAssignMode;

        var opts = new (string Sub, string Base, bool IsCurrent)[]
        {
            ("off",  enabled ? "Désactiver" : "Garder désactivé", !enabled),
            ("noob", "Noob (rééquilibrage)",                      enabled && current == ShAssignMode.Noob),
            ("pgm",  "PGM (récompense)",                          enabled && current == ShAssignMode.Pgm),
            ("rdm",  "Aléatoire",                                 enabled && current == ShAssignMode.Rdm),
        };
        var labelToSub = opts.ToDictionary(o => (o.IsCurrent ? "✓ " : "") + o.Base, o => o.Sub);
        var labels = labelToSub.Keys.ToArray();

        StartVote("votesh", "Vote Superheroes", labels,
            winner =>
            {
                if (!labelToSub.TryGetValue(winner, out var sub)) return;
                SuperheroesModule.ApplyVoteResult(sub);
                var msg = sub switch
                {
                    "off"  => "Vote : Superheroes désactivés.",
                    "noob" => "Vote : Superheroes en mode Noob (rééquilibrage).",
                    "pgm"  => "Vote : Superheroes en mode PGM (récompense).",
                    "rdm"  => "Vote : Superheroes en mode Aléatoire.",
                    _      => $"Vote terminé : {winner}",
                };
                Chat.Broadcast(msg);
            });
    }

    // Point d'entrée pour les votes internes (ex: vote carte Pickup) : réutilise le
    // bulletin chat !vote sans passer par le cooldown joueur. fallbackRandom=true
    // garantit un résultat même si personne ne vote (le Pickup a besoin d'une carte).
    public static void StartChatVote(string kind, string title, IReadOnlyList<string> options,
        Action<string> onWinner, bool fallbackRandom = false)
        => StartVote(kind, title, options, onWinner, fallbackRandom);

    private static void StartVote(string kind, string title, IReadOnlyList<string> options,
        Action<string> onWinner, bool fallbackRandom = false)
    {
        var tally = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var voted = new HashSet<ulong>();
        var capped = options.Take(8).ToList();
        foreach (var o in capped) tally[o] = 0;

        _activeVote = new VoteSession(kind, title, tally, voted, onWinner, capped, fallbackRandom);

        // Annonce dans le chat — aucun menu n'est ouvert, personne n'est figé.
        Chat.Broadcast($"{title} — votez avec \x04!vote <numéro>\x01 ({DurationSec}s) :");
        for (var i = 0; i < capped.Count; i++)
            Chat.Broadcast($"  \x04{i + 1}\x01) {capped[i]}");

        // Auto-close after 30s
        _ = Task.Run(async () =>
        {
            await Task.Delay(DurationSec * 1000);
            Server.NextFrame(FinishVote);
        });
    }

    // !vote <n> : enregistre le choix du joueur pour le vote en cours.
    private static void OnVoteCast(CCSPlayerController player, string[] args)
    {
        var vote = _activeVote;
        if (vote == null)
        {
            Chat.TellError(player, "Aucun vote en cours.");
            return;
        }
        if (args.Length == 0 || !int.TryParse(args[0], out var n) || n < 1 || n > vote.Options.Count)
        {
            Chat.TellError(player, $"Usage : !vote <1-{vote.Options.Count}>");
            return;
        }
        if (!vote.Voted.Add(player.SteamID))
        {
            Chat.TellError(player, "Vous avez déjà voté.");
            return;
        }
        var choice = vote.Options[n - 1];
        vote.Tally[choice]++;
        Chat.TellSuccess(player, $"Vote enregistré : {choice}");
    }

    private static void FinishVote()
    {
        if (_activeVote == null) return;
        var vote = _activeVote;
        _activeVote = null;
        _lastVoteEnd[vote.Kind] = DateTimeOffset.UtcNow;

        // Personne n'a voté.
        if (vote.Tally.Values.Sum() == 0)
        {
            // Vote interne qui exige un résultat (Pickup) : choix aléatoire.
            if (vote.FallbackRandom && vote.Options.Count > 0)
            {
                var pick = vote.Options[new Random().Next(vote.Options.Count)];
                Chat.Broadcast($"{vote.Title} : aucun vote, choix aléatoire : {pick}");
                vote.OnWinner(pick);
                return;
            }
            // Vote joueur : on n'applique rien (évite de changer la map sur 0 voix).
            Chat.Broadcast($"{vote.Title} : aucun vote, annulé.");
            return;
        }
        var winner = vote.Tally.OrderByDescending(kv => kv.Value).First().Key;
        vote.OnWinner(winner);
    }

    private static MapPoolConfig LoadPools()
    {
        // Path canonique du projet : <ModuleDirectory>/../../../configs/plugin.json
        // (ModuleDirectory = addons/counterstrikesharp/plugins/CS2Ultimod, configs/ = addons/configs).
        // AppContext.BaseDirectory pointait sur le dossier du runtime CSSharp, pas du plugin
        // → le fichier était jamais trouvé et le pool restait vide.
        var path = Path.Combine(CS2UltimodPlugin.PluginDirectory, "..", "..", "..", "configs", "plugin.json");
        if (!File.Exists(path))
        {
            CS2UltimodPlugin.Log?.LogWarning("[Vote] plugin.json introuvable au path résolu : {Path}", Path.GetFullPath(path));
            return new MapPoolConfig();
        }
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("MapPools", out var poolsEl)) return new MapPoolConfig();

            var cfg = new MapPoolConfig
            {
                RetakeExecuteMixte = ReadList(poolsEl, "RetakeExecuteMixte"),
                Pickup = ReadList(poolsEl, "Pickup"),
                Arena = ReadList(poolsEl, "Arena"),
                Stuff = [],
            };
            CS2UltimodPlugin.Log?.LogInformation("[Vote] MapPool chargé : Retake/Exec/Mixte={N1} Pickup={N2} Arena={N3}",
                cfg.RetakeExecuteMixte.Count, cfg.Pickup.Count, cfg.Arena.Count);
            return cfg;
        }
        catch (Exception ex)
        {
            CS2UltimodPlugin.Log?.LogError(ex, "[Vote] Erreur de parsing plugin.json");
            return new MapPoolConfig();
        }
    }

    private static List<string> ReadList(JsonElement el, string key)
    {
        if (!el.TryGetProperty(key, out var arr)) return [];
        return arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
    }

    private sealed record VoteSession(
        string Kind,
        string Title,
        Dictionary<string, int> Tally,
        HashSet<ulong> Voted,
        Action<string> OnWinner,
        // Options ordonnées : l'index (1-based) saisi par le joueur mappe ici.
        IReadOnlyList<string> Options,
        // true : si personne ne vote, tire une option au hasard (vote interne Pickup).
        bool FallbackRandom);
}

internal sealed class MapPoolConfig
{
    public List<string> RetakeExecuteMixte { get; set; } = [];
    public List<string> Pickup { get; set; } = [];
    public List<string> Arena { get; set; } = [];
    public List<string> Stuff { get; set; } = [];
}
