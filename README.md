# Aetheria

Un MMORPG fantasy hardcore, en 3D isométrique (PvE + PvP), en C#, conçu **serveur-autoritaire dès le
départ** pour un monde **sans coupure**. Ce dépôt contient le cœur serveur + réseau et un client de
test headless. Le client de rendu Unity viendra plus tard (voir la [ROADMAP](docs/ROADMAP.md)).

> **Statut : M0 — walking skeleton (tranche verticale).** Un serveur autoritaire à pas de temps fixe
> simule un monde continu ; les clients se connectent en UDP, envoient leurs intentions de
> déplacement, et reçoivent des snapshots filtrés par zone d'intérêt (AoI). Deux clients se voient
> bouger en temps réel et disparaissent du champ de vision l'un de l'autre quand ils quittent le rayon
> d'intérêt. C'est la fondation sur laquelle tout le reste se construit.

## Pourquoi c'est fait comme ça

Le plus dur dans un MMO, ce n'est jamais les classes ou l'art — c'est le *massively*. Deux décisions
ici protègent contre le piège classique (construire un monde immense et ne jamais rien livrer) :

- **Le serveur est autoritaire.** Les clients envoient une *intention* ; le serveur décide de ce qui
  se passe. Non négociable pour du PvP hardcore — le client n'est jamais cru. Voir
  [ADR-0001](docs/adr/0001-authoritative-server.md).
- **L'interest management dès le premier commit.** Le serveur simule tout, mais n'informe chaque
  client que des entités proches de lui, via une grille spatiale. C'est ce qui permet à un monde
  continu de passer à l'échelle, et c'est le même point d'accroche qui rendra le server meshing
  possible ensuite. Voir [ADR-0002](docs/adr/0002-interest-management.md).

Lis [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) pour la vision complète, y compris le chemin qui mène
d'un nœud unique sans coupure à un monde maillé, réellement massif.

## Prérequis

- [SDK .NET 10.0](https://dotnet.microsoft.com/download) ou plus récent.
- N'importe quel éditeur : Rider, Visual Studio 2022 (17.13+ pour le `.slnx`), ou VS Code + C# Dev Kit.

## Compiler, tester, lancer

```bash
# depuis la racine du dépôt
dotnet build Aetheria.slnx -c Release      # compile proprement ; les warnings sont des erreurs

dotnet run --project tests/Aetheria.Tests -c Release   # lance les tests unitaires

# Terminal 1 — démarre le serveur autoritaire (UDP 27015 par défaut)
dotnet run --project src/Aetheria.Server -c Release

# Terminaux 2 et 3 — connecte des clients de test headless
dotnet run --project src/Aetheria.Client.TestHarness -c Release -- --name Aria  --dirx 1  --seconds 20
dotnet run --project src/Aetheria.Client.TestHarness -c Release -- --name Borin --dirx -1 --seconds 20
```

Chaque client affiche, une fois par seconde, les entités présentes dans sa zone d'intérêt, et tu peux
observer l'autre joueur entrer puis sortir de cet ensemble à mesure qu'ils s'éloignent.

### Options du client de test

| Option      | Signification                              | Défaut      |
|-------------|--------------------------------------------|-------------|
| `--host`    | Hôte du serveur                            | `127.0.0.1` |
| `--port`    | Port UDP du serveur                        | `27015`     |
| `--name`    | Nom affiché, envoyé lors du handshake      | `tester`    |
| `--seconds` | Durée de connexion                         | `8`         |
| `--dirx`    | Intention de déplacement en X (-1..1)      | `0`         |
| `--diry`    | Intention de déplacement en Y (-1..1)      | `0`         |

## Structure du projet

```
Aetheria.slnx
├── src/
│   ├── Aetheria.Shared/            # code partagé serveur & client
│   │   ├── Math/                   #   Vec2 (vecteur sur le plan du monde)
│   │   ├── Spatial/                #   SpatialGrid — interest management
│   │   ├── Protocol/               #   format réseau : PacketReader/Writer, messages
│   │   └── Net/                    #   abstraction ITransport + implémentation UDP brute
│   ├── Aetheria.Server/            # serveur autoritaire headless
│   │   ├── World/                  #   World, ServerEntity (la source de vérité)
│   │   ├── GameServer.cs           #   liaison réseau <-> monde, handshake, snapshots
│   │   └── FixedStepLoop.cs        #   boucle déterministe à pas de temps fixe
│   └── Aetheria.Client.TestHarness/# client headless pour les tests (sans rendu)
└── tests/
    └── Aetheria.Tests/             # tests unitaires sans dépendance (grille, protocole, monde)
```

## À propos des dépendances

Le squelette n'a **aucune dépendance NuGet externe** — il compile et tourne avec le seul SDK .NET.
C'est un choix délibéré pour un démarrage propre ([ADR-0004](docs/adr/0004-zero-dependency-skeleton.md)) ;
les premières tâches de la roadmap remplacent les pièces faites maison par des bibliothèques
éprouvées (LiteNetLib pour l'UDP fiable, xUnit pour les tests) derrière les interfaces déjà en place.

## Licence

À définir — à ajouter avant toute diffusion publique.
