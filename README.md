# CS2Ultimod

Plugin CS2 unifié pour CounterStrikeSharp. Regroupe en un seul plugin :

- **Retake** — spawns A/B + planteur auto + rotation T/CT + équilibrage + instadefuse + éditeur de spawns
- **Execute** — scenarios + spawns + lineups de nades + éditeur in-game complet (4 maps prêtes : Mirage, Ancient, Anubis, Nuke)
- **Mixte** — alternance random retake/execute par round
- **Stuff** — entraînement nades (money infini, respawn instant, `!clear`, `!addbot`)
- **Pickup** — match 5v5 MR12 (capitaine / random / elo Faceit, knife round, ready/pause, BO3 vote)
- **Superheroes** — pouvoirs aléatoires basés sur perf (`!sh noob/pgm/rdm`)
- **Admin** — kick/ban/mute/slay/menu navigable + map pool + switch de mode

Voir [SCOPE.md](./SCOPE.md) pour le périmètre V1 figé et [INTERFACES.md](./INTERFACES.md) pour les contrats internes.

## Build

```
dotnet build src/CS2Ultimod.csproj -c Release
```

## Install (Dathost)

Copier `bin/Release/net8.0/CS2Ultimod.dll` (et dépendances) dans :
```
addons/counterstrikesharp/plugins/CS2Ultimod/
```

Configs et DB SQLite vont dans `addons/counterstrikesharp/plugins/CS2Ultimod/data/` (créé au premier lancement).

## Contribuer

Les contributions sont bienvenues. Voir [CONTRIBUTING.md](./CONTRIBUTING.md) pour le build, l'architecture et le workflow de Pull Request.

Les identifiants de serveur (FTP, RCON, API) ne sont jamais commités : ils vont dans un fichier `.env` local, modelé sur [.env.example](./.env.example).

## Licence

Tous droits réservés. Code visible publiquement pour référence et contribution, mais pas open-source : pas de réutilisation, copie, redistribution ou revente sans accord écrit. Voir [LICENSE](./LICENSE).

Sources externes intégrées (sous MIT, leurs portions restent MIT) :
- [B3none/cs2-retakes](https://github.com/B3none/cs2-retakes)
- [B3none/cs2-instadefuse](https://github.com/B3none/cs2-instadefuse)
- [bazookaCodes/cs2-executes-plugin](https://github.com/bazookaCodes/cs2-executes-plugin)
- [yonilerner/cs2-retakes-allocator](https://github.com/yonilerner/cs2-retakes-allocator)
- Configs map execute portées depuis [zwolof/cs2-executes](https://github.com/zwolof/cs2-executes)
