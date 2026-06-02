# SCOPE V1 — figé

> Document figé après scoping. Toute modification = décision explicite utilisateur.

## Architecture
- **Plateforme :** CounterStrikeSharp (CSS) — C# / .NET 8
- **Stockage :** SQLite local (`data/plugin.db`)
- **5 modes mutuellement exclusifs :** retake (défaut au boot), execute, mixte, stuff, pickup
- **Switch de mode :** `!mode <x>` (admin) ou `!votemode` (joueurs)
- **Stratégie code :**
  - **Fork-merge MIT** : B3none/cs2-retakes + B3none/cs2-instadefuse + bazookaCodes/cs2-executes-plugin + yonilerner/cs2-retakes-allocator
  - **From scratch** : couche admin (inspirée SimpleAdmin GPL → réimpl pour éviter contamination), superheroes, votes, switch de mode, mode pickup, mode mixte, mode stuff
  - **Configs map execute** : 4 JSON portés depuis zwolof via script de migration (int IDs → Guid)

## Mode RETAKE
- Spawns A/B (B3none) + éditeur intégré (`!edit`, `!add`, `!remove`, `!nearest`, `!done`)
- Planteur auto, rotation T/CT, équilibrage équipes
- Instadefuse (exclusif retake)
- Allocator refondu (préférences SQLite, menu framework)
- Superheroes activable

## Mode EXECUTE
- Moteur bazookaCodes (scenarios + spawns + grenades + replay)
- Éditeur in-game complet : `!debug`, `!addspawn`, `!createscenario`, `!addtspawntoscenario`, `!addctspawntoscenario`, `!addgrenadetoscenario`, `!runscenario`...
- 4 maps prêtes : Mirage / Ancient / Anubis / Nuke (portées zwolof)
- Mode dégradé sur autres maps : spawns retake + full util T imposé
- Allocator + superheroes actifs

## Mode MIXTE
- Random 50/50 retake/execute par round
- Pool maps partagé retake/execute/mixte
- Allocator + superheroes actifs

## Mode STUFF
- Pas de bots / bombe / timer
- Money infini, achat partout
- Respawn instant à côté du lieu de mort
- `!clear` (nettoyage nades)
- `!addbot` (mannequin statique à la position joueur)
- Toutes maps autorisées (pas de pool)

## Mode PICKUP
- Format MR12, knife/taser round
- 3 modes compo : capitaine (pick&ban map par capitaines), random, elo Faceit
- Random/elo : vote map + étape shuffle équipes avec validation
- Liaison SteamID ↔ Faceit via `!faceit <pseudo>` (SQLite + API Faceit)
- `!ready` / `!ready -f`, `!pause` / `!unpause`
- Pool maps dédié pickup
- Fin de match : warmup + vote BO3
  - Oui → mêmes équipes, pick&ban des 2 maps suivantes
  - Non → reste en warmup, attend action admin

## Superheroes (retake / execute / mixte / pickup)
- Pouvoirs : taille skin, vitesse, HP, shield, nb utilities, dégâts, xray, radar ennemis
- 3 modes : `!sh noob` (rattrapage) / `!sh pgm` (récompense) / `!sh rdm` (random)
- Retake/execute/mixte : activation à la volée, applique au round suivant, désactivable à tout moment
- Pickup : choisi au début du pickup, persiste toute la game. Désactivation = restart game + warning + double confirmation
- Ne persiste pas au changement de mode ni au restart serveur
- **Réimplémenté from scratch** (sources daniel-Jones et Kandru = inspiration uniquement, licences non-OK pour copy)

## Admin transverse
**Réimplémenté from scratch** (inspiré SimpleAdmin sans contamination GPL).
Menu navigable façon SimpleAdmin (inputs jeu).

**Commandes gardées :**
- Modération : kick, ban, banip, addban, unban, gag, ungag, mute, unmute, silence, unsilence
- Punitions : slay, slap, freeze, unfreeze, noclip, respawn, god
- Joueurs : team (move), swap, rename, who, players, disconnected
- Serveur : map, changemap, wsmap, rcon, cvar, rr, rg, extend, hsay, say, psay, csay
- Admin mgmt : addadmin, deladmin, reladmin
- Divers : admin (menu), help, stats

**Commandes virées :**
- Système warnings (warn/unwarn/warns)
- Groupes admin (addgroup/delgroup)

## Map pool
- **Pool retake/execute/mixte** (partagé)
- **Pool pickup** (dédié, maps compétitives)
- **Stuff** : pas de pool, toutes maps serveur
- Switch mode sur même map : recharge config standard, admin réajuste

## Votes joueurs
- `!votemode`, `!votemap`
- Seuils (majorité, cooldown) : à définir avant impl, pas avant

## EXCLU V1 → V2
- MySQL (SQLite seul suffit, 1 serveur)
- RTV original (votemap maison à la place)
- Menu d'achat natif CS2 pour allocator (priorité haute V2)
- Save/load lineups stuff
- Stats fin de match pickup
- Bots IA en stuff (mannequins seulement)
- Web panel admin

## Plan de sprint
Voir [PLAN.md](./PLAN.md). Stratégie : main session pour Phase 0 + 3 agents en parallèle (Execute, Pickup, Admin) + main session sur Retake/Allocator/Votes/Superheroes/Stuff/Mixte + Phase 2 intégration.
