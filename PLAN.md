# PLAN.md — Sprint V1

## Découpage

### Phase 0 — Foundation (main session, séquentiel)
- F1 Skeleton projet ✅
- F2 SQLite layer + migrations
- F3 Mode manager + event bus mode-aware
- F4 Menu framework
- F5 Permission system
- F6 Common utils (chat, players, commands registry)
- ✅ INTERFACES.md figé et validé par utilisateur

### Phase 1 — Tracks parallèles

**Délégué à 3 agents :**
- **Agent 1 — Track B (Execute)** : port bazookaCodes + éditeur + 4 maps zwolof migrées + mode dégradé
- **Agent 2 — Track H (Pickup)** : MR12 + capitaine/random/elo Faceit + ready/pause + BO3 vote
- **Agent 3 — Track D (Admin)** : reimpl from scratch des 30 commandes + menu admin + DB bans/mutes

**Main session pendant que les agents bossent :**
- Track A (Retake) → port B3none + instadefuse
- Track E (Allocator) → port yonilerner + refonte SQLite + menu framework
- Track G (Votes) → vote framework + votemode/votemap + map pools
- Track F (Superheroes) → reimpl from scratch
- Track C (Stuff) → from scratch (le plus simple, en dernier)

### Phase 2 — Intégration (main session)
- I1 Mode mixte (random scheduler retake/execute)
- I2 Branchement superheroes sur tous les modes
- I3 Branchement allocator sur execute & mixte
- I4 Branchement votes
- Review/cleanup code des agents
- Tests E2E sur Dathost

## Jalons testables

1. **Fin Phase 0** : plugin se charge, log "ModeManager: retake active", `!admin` ouvre menu vide, SQLite créé
2. **Fin Track A + D (main + Agent 3)** : retake + admin jouables — première milestone live
3. **Fin Track B + I1** : execute + mixte jouables — deuxième milestone
4. **Fin Track H** : pickup jouable end-to-end (1 match complet sans crash) — troisième milestone
5. **Fin Phase 2** : V1 complète, smoke test full

## Chronologie estimée

- Phase 0 : ~1 session
- Phase 1 : agents en parallèle + main session séquentielle (~3-5 sessions)
- Phase 2 : ~1-2 sessions

## Risques de parallélisation

1. **Drift d'interface** : INTERFACES.md verrouillé, briefs agents incluent le doc
2. **Style hétérogène** : 3 agents → 3 styles, harmonisé en Phase 2
3. **Découverte tardive** : check-in à mi-parcours via SendMessage si agent > 30 min
4. **Conflits SQLite** : préfixes de tables réservés (voir INTERFACES.md §1)
5. **Conflits commandes** : registry central refuse les collisions au boot
