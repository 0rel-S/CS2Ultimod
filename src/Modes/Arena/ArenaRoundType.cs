using CounterStrikeSharp.API.Modules.Entities.Constants;

namespace CS2Ultimod.Modes.Arena;

// Un preset d'armes pour un round d'arène. Les deux joueurs reçoivent le même
// équipement (duel équitable). Primary nullable = round pistolet/deagle uniquement.
public sealed record ArenaRoundType(
    string Name,
    CsItem? Primary,
    CsItem Secondary,
    bool Helmet = true)
{
    // Pool tiré au hasard à chaque assignation d'arène. Volontairement compact :
    // les classiques du 1v1 (rifle, AWP, scout, deagle, pistolet, SMG).
    public static readonly ArenaRoundType[] All =
    [
        new("Fusil",    CsItem.AK47,  CsItem.Deagle),
        new("AWP",      CsItem.AWP,   CsItem.Deagle),
        new("Scout",    CsItem.Scout, CsItem.Deagle),
        new("SMG",      CsItem.Mac10, CsItem.P250),
        new("Deagle",   null,         CsItem.Deagle),
        new("Pistolet", null,         CsItem.USPS),
    ];
}
