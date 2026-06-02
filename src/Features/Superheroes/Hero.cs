namespace CS2Ultimod.Features.Superheroes;

// Noob = rattrapage (top → nerf, bottom → buff)
// Pgm  = récompense (top → buff, bottom → nerf)
// Rdm  = random
public enum ShAssignMode { Noob, Pgm, Rdm }

// Tier: -2 = gros nerf, -1 = petit nerf, 0 = neutre, +1 = petit buff, +2 = gros buff
public enum HeroEffect
{
    None,
    BonusHp,        // Param = HP delta (peut être négatif)
    BonusArmor,     // Param = armor delta
    SpeedMul,       // Param = % delta (ex: 40 = +40%)
    ModelScale,     // Param = % (ex: 70 = 0.70x, 140 = 1.40x)
    ExtraFlash,     // Param = nb
    ExtraHE,        // Param = nb
    ExtraSmoke,     // Param = nb
    ExtraMolotov,   // Param = nb
    NoNades,        // pas de param
    Regen,          // Param = HP/s
    Radar,          // ennemis visibles sur la minimap
    Xray,           // ennemis visibles à travers les murs (glow)
    DamageTaken,    // Param = % (ex: 150 = prend 1.5x)
    Hypnose,        // give 1 décoy au spawn ; au lancé, freeze les ennemis Param secondes
    Invisible,      // pawn model alpha 0 (arme tenue reste visible) — hitbox conservée
    Spy,            // strip armes + nades, knife only, scale 70, speed +100
}

public sealed record Hero(string Id, string Name, string Description, int Tier, HeroEffect Effect, int Param = 0);

public static class HeroCatalog
{
    public static readonly Hero[] All =
    [
        // ── -2 : gros nerfs (top fragger) ──
        new("geant",     "Géant",      "hitbox 2×",         -2, HeroEffect.ModelScale, 200),
        new("verre",     "Verre",      "50 HP max",         -2, HeroEffect.BonusHp,    -50),
        new("fragile",   "Fragile",    "encaisse 1.5× dmg", -2, HeroEffect.DamageTaken, 150),
        new("escargot",  "Escargot",   "-40% speed",        -2, HeroEffect.SpeedMul,   -40),

        // ── -1 : petits nerfs ──
        new("lent",      "Lent",       "-20% speed",        -1, HeroEffect.SpeedMul,   -20),
        new("nu",        "Nu",         "0 armor",           -1, HeroEffect.BonusArmor, -200),
        // Manchot retiré temporairement : crash serveur reproductible quand StripNades
        // s'exécute juste après le spawn (cf. logs 2026-05-07 22:42). À réactiver une
        // fois la séquence StripNades fiabilisée (delay > NextFrame, ou drop avant kill).
        new("anemique",  "Anémique",   "-30 HP",            -1, HeroEffect.BonusHp,    -30),
        new("grand",     "Grand",      "hitbox 1.3×",       -1, HeroEffect.ModelScale, 130),

        // ── 0 : neutres ──
        new("normal",    "Normal",     "rien de spécial",    0, HeroEffect.None,       0),
        new("quidam",    "Quidam",     "RAS",                0, HeroEffect.None,       0),

        // ── +1 : petits buffs ──
        new("costaud",   "Costaud",    "+30 HP",             1, HeroEffect.BonusHp,    30),
        new("blinde",    "Blindé",     "+50 armor",          1, HeroEffect.BonusArmor, 50),
        new("rapide",    "Rapide",     "+20% speed",         1, HeroEffect.SpeedMul,   20),
        new("aveuglant", "Aveuglant",  "+2 flash",           1, HeroEffect.ExtraFlash, 2),
        new("boum",      "Boum",       "+2 HE",              1, HeroEffect.ExtraHE,    2),
        new("fumeur",    "Fumeur",     "+2 smoke",           1, HeroEffect.ExtraSmoke, 2),
        new("pyromane",  "Pyromane",   "+2 molotov",         1, HeroEffect.ExtraMolotov, 2),

        // ── +2 : gros buffs (bottom fragger) ──
        new("minus",     "Minus",      "hitbox 0.7×",        2, HeroEffect.ModelScale, 70),
        new("eclair",    "Éclair",     "+40% speed",         2, HeroEffect.SpeedMul,   40),
        new("tank",      "Tank",       "+100 HP",            2, HeroEffect.BonusHp,    100),
        new("regen",     "Régen",      "+5 HP/s",            2, HeroEffect.Regen,      5),
        new("radar",     "Radar",      "ennemis sur radar",  2, HeroEffect.Radar,      0),
        new("xray",      "Rayon-X",    "voir à travers les murs", 2, HeroEffect.Xray,  0),
        new("hypnose",   "Hypnose",    "décoy → freeze ennemis 3s", 2, HeroEffect.Hypnose, 3),
        new("invisible", "Invisible",  "modèle invisible (arme visible)", 2, HeroEffect.Invisible, 0),
        new("spy",       "Spy",        "couteau only, hitbox 0.5×, speed ×2", 2, HeroEffect.Spy, 0),
    ];

    public static Hero[] OfTier(int tier) => All.Where(h => h.Tier == tier).ToArray();
}
