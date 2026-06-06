# CSSharp / CS2 — pièges & techniques natives (notes de travail)

Notes accumulées en debuggant CS2Ultimod. But : ne pas re-découvrir à chaque fois comment
CounterStrikeSharp (CSSharp) et le moteur CS2 se comportent. **À compléter à chaque nouvel apprentissage.**
Sources vérifiées datées entre crochets.

---

## SetStateChanged : NO-OP silencieux si le champ n'est pas networké  [2026-06-07, source CSSharp]

`Utilities.SetStateChanged(entity, "ClassName", "fieldName")` :
```csharp
if (!Schema.IsSchemaFieldNetworked(className, fieldName)) {
    Logger.LogWarning("Field {ClassName}:{FieldName} is not networked, but SetStateChanged was called on it.");
    return;   // <-- AUCUNE écriture réseau, retour immédiat
}
```
- Si tu vois ce warning en boucle, **ton SetStateChanged ne fait rien** : la valeur est écrite côté serveur mais jamais propagée aux clients.
- Toujours vérifier avec `Schema.IsSchemaFieldNetworked(cls, field)` (renvoie bool) avant de supposer qu'un champ se réplique.
- ⚠ `Schema` est dans le namespace **`CounterStrikeSharp.API.Modules.Memory`** (pas `.Core`). Idem `Schema.GetSchemaValue` / `SetSchemaValue`. [CSSharp 1.0.367]
- Le bon (classe, champ) dépend de la classe où le netvar est **déclaré** : un champ peut être non-networké sous la classe de base et networké sous la classe dérivée (ex. `CCSPlayerPawnBase` vs `CCSPlayerPawn`).

## Radar / spotting (ennemis sur la minimap)  [2026-06-07]

- L'état est dans `pawn.EntitySpottedState` (`CEntitySpottedState_t`), champ `m_entitySpottedState` du pawn.
  - `m_bSpotted` (bool) = spotté ou non.
  - `m_bSpottedByMask` (uint[2], 64 bits) = bitmask par **slot joueur** : bit N set ⇒ le joueur slot N voit ce pawn sur son radar.
- Pour que ça se propage : écrire le mask **puis** `SetStateChanged` sur un champ networké (cf. piège ci-dessus). `CCSPlayerPawnBase:m_entitySpottedState` n'est **pas** networké → essayer `CCSPlayerPawn`.
- **Temps réel** : un blip radar n'est mis à jour qu'au moment où on (re)spotte. Rafraîchir toutes les 0.4s donne un rendu « statique/saccadé ». Pour suivre le mouvement en continu, re-spotter à haute fréquence (≈ chaque tick / 0.05s).

## Vitesse de déplacement  [2026-06-07, réfs CS2-SimpleAdmin + CSSharp issue #564]

- `pawn.VelocityModifier` (`m_flVelocityModifier`) = **multiplicateur** standard de vitesse (SimpleAdmin FunCommands l'utilise pour sa commande speed). 1.0 = normal, 2.0 = ×2.
- `MovementServices.MaxSpeed` / `m_flMaxspeed` ne permet que de **baisser** la vitesse (clampé par l'arme) — inutilisable pour accélérer. Issue CSSharp #564.
- Le moteur **reset `VelocityModifier` à 1.0** sur certains events (notamment le saut). Si on ne le réécrit que toutes les 0.05s (3 ticks @64), il y a une fenêtre où le buff disparaît → ressenti « pas de buff au saut ». **Réécrire chaque tick** (listener `OnTick`) supprime ce trou.
- En l'air CS2 n'a pas de friction horizontale : un joueur buffé qui saute en pleine course **conserve** sa vélocité. Le buff se ressent donc surtout via la vitesse au sol au décollage → la garder constante (per-tick) est l'essentiel.

## Glow « à travers les murs » (wallhack/Xray)  [2026-06-07, réf labaland/plugin-wallhack]

- Pas le spotting : on crée **2 `prop_dynamic`** par cible (un *relay* invisible qui suit le pawn via `FollowEntity`, un *glow* qui suit le relay), `Glow.GlowType = 3`, `GlowRange = 5000`, `GlowColorOverride`, `GlowTeam` = équipe ennemie.
- Visibilité par viewer via un listener `CheckTransmit` (on add/remove l'entité glow de `TransmitEntities` selon qui doit la voir).
- Refresh ≈ 0.5s + délai 0.20s après spawn (laisser le model loader finir avant de lire le `.vmdl`).
- L'implémentation `XrayGlow` de CS2Ultimod est calquée sur ce pattern (identique à labaland).

## Entités & armes : éviter les crashs natifs  [2026-05/06, logs serveur]

- `GiveNamedItem` / `RemoveWeapons` sur un pion **mort ou invalide** = crash natif (pas d'exception .NET). Toujours garder `pawn != null && pawn.IsValid && player.PawnIsAlive` — surtout après un `await` suivi d'un `Server.NextFrame` (le pion peut être mort entre-temps).
- `weapon.Remove()` direct sur une entité = dangling pointer → crash natif. Utiliser `entity.AcceptInput("Kill")` pour deleter proprement (décrémente les refs du game manager).
- `SetScale` per-entity : `pawn.AcceptInput("SetScale", null, null, "1.4")`. NE PAS utiliser `SkeletonInstance.Scale` (partagé entre pions du même model resource → scale tout le monde).
- CS2 reset silencieusement `m_MoveType` et `m_flVelocityModifier` chaque tick après certains events → réécrire à haute fréquence si on veut que l'effet tienne.

---
_Voir aussi `INTERFACES.md` (API interne du projet) et le code `src/Features/Superheroes/`._
