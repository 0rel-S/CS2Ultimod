# Contribuer à CS2Ultimod

Merci de vouloir contribuer ! Ce guide couvre l'essentiel pour démarrer.

## Prérequis

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Un serveur CS2 avec [CounterStrikeSharp](https://docs.cssharp.dev/) installé pour tester en jeu
- Python 3 (uniquement pour les scripts de déploiement dans `tools/`)

## Build

```
dotnet build src/CS2Ultimod.csproj -c Release
```

La DLL est produite dans `src/bin/Release/net8.0/CS2Ultimod.dll`.

## Architecture

- [SCOPE.md](./SCOPE.md) — périmètre et liste des fonctionnalités
- [INTERFACES.md](./INTERFACES.md) — contrats internes (events, menus, helpers)
- [PLAN.md](./PLAN.md) — état d'avancement

Le code est organisé par mode dans [src/Modes/](./src/Modes/), avec le socle commun dans [src/Core/](./src/Core/).

## Déploiement sur ton serveur (optionnel)

Les scripts `tools/deploy_*.py` automatisent build + upload FTP + reload.
Ils lisent tes identifiants depuis un fichier `.env` à la racine, **jamais commité** :

```
cp .env.example .env
# puis édite .env avec les valeurs de TON serveur
```

- `python tools/deploy_hot.py` — reload à chaud, les joueurs restent connectés
- `python tools/deploy_hard.py` — restart complet (déconnecte les joueurs, état propre)
- `python tools/rcon.py "command"` — envoie une commande RCON

## Workflow de contribution

1. Fork le repo (ou crée une branche si tu as accès en écriture)
2. Crée une branche par fonctionnalité : `git checkout -b ma-fonctionnalite`
3. Commits clairs, en français ou anglais
4. Ouvre une Pull Request vers `main` en décrivant le changement

## Sécurité

Ne commit **jamais** d'identifiants (FTP, RCON, API, clé Faceit). Tout secret
passe par `.env` (gitignored). Si tu repères un secret commité par erreur,
signale-le en privé plutôt que dans une issue publique.
