using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2Ultimod.Modes.Arena;

// Snapshot d'une paire de spawns (positions/angles copiés à la détection, pas de
// handle d'entité conservé → stable sur toute la durée de la map).
public readonly record struct ArenaSpawnPair(Vector PosA, QAngle AngA, Vector PosB, QAngle AngB);

public enum ArenaResultType { Win, NoOpponent, Empty }

public readonly record struct ArenaResult(ArenaResultType Type, CCSPlayerController? Winner, CCSPlayerController? Loser);

// Une arène 1v1 : 2 joueurs max, suit les kills du round, calcule le vainqueur,
// téléporte et équipe ses joueurs à chaque spawn.
public sealed class Arena
{
    private readonly ArenaSpawnPair _spawns;

    public int Rank { get; private set; }
    public ArenaRoundType RoundType { get; private set; } = ArenaRoundType.All[0];

    public CCSPlayerController? Player1 { get; private set; }
    public CCSPlayerController? Player2 { get; private set; }

    private int _kills1;
    private int _kills2;
    // Départage en cas d'égalité de kills : le dernier à avoir tué l'emporte.
    private bool _player1HasLastKill = true;
    // Inversion aléatoire des 2 spawns chaque round (varie les positions).
    private bool _swapSpawns;

    public Arena(ArenaSpawnPair spawns) => _spawns = spawns;

    public void Assign(CCSPlayerController? p1, CCSPlayerController? p2, int rank, ArenaRoundType roundType, bool swapSpawns)
    {
        Player1 = p1;
        Player2 = p2;
        Rank = rank;
        RoundType = roundType;
        _swapSpawns = swapSpawns;
        _kills1 = 0;
        _kills2 = 0;
        _player1HasLastKill = true;

        if (IsValid(p1)) AssignTeam(p1!, CsTeam.Terrorist);
        if (IsValid(p2)) AssignTeam(p2!, CsTeam.CounterTerrorist);
    }

    // SwitchTeam ne fonctionne que pour permuter entre T et CT. Pour un joueur en
    // Spectator/None (un arrivant), il faut ChangeTeam. (CsTeam : None=0, Spec=1, T=2, CT=3.)
    private static void AssignTeam(CCSPlayerController p, CsTeam team)
    {
        if (p.Team > CsTeam.Spectator) p.SwitchTeam(team);
        else p.ChangeTeam(team);
    }

    public bool Contains(CCSPlayerController player) =>
        (Player1 != null && Player1 == player) || (Player2 != null && Player2 == player);

    // Au moins un vrai joueur (non-bot) assigné. Sert à ne compter que les arènes
    // "actives" pour la fin de round : une arène 100 % bots ne doit pas la déclencher,
    // et un bot de sparring ne suffit pas à rendre une arène active.
    public bool HasRealPlayers => IsReal(Player1) || IsReal(Player2);

    // Le duel est résolu. Arène vide → fini. Joueur seul (pas d'adversaire valide) →
    // fini (personne ne peut le tuer, il ne doit pas bloquer la fin de round). Deux
    // joueurs valides → fini dès que l'un des deux est mort. À n'évaluer qu'en cours
    // de round (sur mort), sinon les pawns pas encore spawn donneraient un faux "fini".
    public bool HasFinished
    {
        get
        {
            bool p1 = IsValid(Player1);
            bool p2 = IsValid(Player2);
            if (!p1 && !p2) return true;
            if (p1 ^ p2) return true;
            return !Player1!.PawnIsAlive || !Player2!.PawnIsAlive;
        }
    }

    // Un joueur de cette arène est mort → son adversaire marque le kill.
    public void OnDeath(CCSPlayerController victim)
    {
        if (Player1 != null && Player1 == victim) { _kills2++; _player1HasLastKill = false; }
        else if (Player2 != null && Player2 == victim) { _kills1++; _player1HasLastKill = true; }
    }

    public ArenaResult GetResult()
    {
        var p1 = IsValid(Player1) ? Player1 : null;
        var p2 = IsValid(Player2) ? Player2 : null;

        if (p1 != null && p2 != null)
        {
            bool p1Wins = _kills1 > _kills2 || (_kills1 == _kills2 && _player1HasLastKill);
            return p1Wins
                ? new ArenaResult(ArenaResultType.Win, p1, p2)
                : new ArenaResult(ArenaResultType.Win, p2, p1);
        }
        if (p1 != null) return new ArenaResult(ArenaResultType.NoOpponent, p1, null);
        if (p2 != null) return new ArenaResult(ArenaResultType.NoOpponent, p2, null);
        return new ArenaResult(ArenaResultType.Empty, null, null);
    }

    // Téléporte + équipe le joueur qui vient de spawn (appelé sur main thread).
    public void SpawnPlayer(CCSPlayerController player)
    {
        bool isP1 = Player1 != null && Player1 == player;
        bool isP2 = Player2 != null && Player2 == player;
        if (!isP1 && !isP2) return;

        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return;

        // P1 prend le spawn A, P2 le spawn B (inversé si _swapSpawns).
        bool useA = isP1 ^ _swapSpawns;
        var pos = useA ? _spawns.PosA : _spawns.PosB;
        var ang = useA ? _spawns.AngA : _spawns.AngB;
        pawn.Teleport(new Vector(pos.X, pos.Y, pos.Z), new QAngle(ang.X, ang.Y, ang.Z), new Vector(0, 0, 0));

        pawn.Health = 100;

        player.RemoveWeapons();
        if (RoundType.Primary is { } primary) player.GiveNamedItem(primary);
        player.GiveNamedItem(RoundType.Secondary);
        player.GiveNamedItem(CsItem.Knife);
        player.GiveNamedItem(RoundType.Helmet ? CsItem.KevlarHelmet : CsItem.Kevlar);
    }

    // Combattant valide (réel OU bot de sparring). Les bots ne portent pas d'état
    // Connected fiable (cf. K4-Arenas qui ne le vérifie pas pour eux) : on se contente
    // de IsValid pour un bot, et on exige Connected pour un vrai joueur (filtre les
    // déconnexions en cours).
    private static bool IsValid(CCSPlayerController? p) =>
        p is { IsValid: true } && (p.IsBot || p.Connected == PlayerConnectedState.Connected);

    private static bool IsReal(CCSPlayerController? p) => IsValid(p) && !p!.IsBot;
}
