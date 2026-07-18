# Aetheria

Un MMORPG fantasy hardcore, en 3D isométrique (PvE + PvP), en C#, conçu **serveur-autoritaire dès le
départ** pour un monde **sans coupure**. Ce dépôt contient le cœur serveur + réseau, un client de
test headless, et le **client Unity isométrique jouable** (`unity/AetheriaClient` — voir
[unity/README.md](unity/README.md) pour le lancer en 5 minutes).

> **Statut : M0–M2 + M3 étapes A, B & E.** En plus du système de personnage : **objets & équipement**
> (bonus de stats), **cadavres full-loot** (à la mort, l'inventaire + l'équipement + l'or tombent au
> sol, lootables par n'importe qui, le corps disparaît une fois vidé), **or**, et **progression** —
> l'XP des kills fait **évoluer les stats en continu** (attaque/défense/**PV max et mana max** ; rage
> et énergie restent à 100), quelques niveaux débloquent des capacités avancées, et chaque style
> d'arme/sort a une **maîtrise qui monte à l'usage** et le rend plus puissant (façon WoW d'origine).
> Les **noms de personnage sont uniques par serveur** (les deux factions). **Hardcore** : à la mort le
> personnage **repart de zéro** (XP/niveau/compétences remis à zéro, équipement lâché sur le cadavre),
> mais une **banque de compte persistante** garde or et objets d'une vie à l'autre. **Groupes** (invite
> même faction, chef, cap 40) et **instances** : donjons instanciés **scalés selon la taille du
> groupe**, **raids 6–40 joueurs** (instanciés, scalés), tandis que les **donjons du monde ouvert ne
> sont PAS instanciés** (camp du Roi Gobelin) et qu'un **boss de raid mondial** (Ashmaw) rôde en monde
> ouvert — **PvP possible** : on ne peut pas attaquer son propre camp, mais la faction adverse oui.
> **Persistance (M4)** : comptes avec **secret** (SHA-256), **noms possédés durablement**, personnages
> (XP, or, inventaire, équipement, maîtrises) et **banques sauvegardés sur disque** (fichier JSON
> atomique derrière une interface — Postgres s'y branchera) ; sauvegarde périodique, à la déconnexion
> et **immédiate à chaque mort**. Vérifié : le serveur est tué puis redémarré — personnage restauré,
> banque intacte, mauvais secret rejeté, vol de nom rejeté. Et le **client Unity** est jouable :
> bootstrap zéro-config, caméra iso, entités interpolées, HUD complet (voir `unity/README.md`).
>
> Le socle (système de personnage) : un serveur autoritaire à pas de temps fixe
> simule un monde continu ; les clients se connectent en UDP, choisissent **faction / race / classe /
> genre**, se déplacent, et reçoivent des snapshots filtrés par zone d'intérêt (AoI) incluant santé,
> faction et ressource. Le combat est autoritaire (capacités avec portée/cooldown/**coût de
> ressource**, dégâts fonction des stats *effectives*), avec mort et respawn. Deux **factions**
> (Alliance : Humain, Nain · Horde : Orc, Elfe), une **matrice classe/race** validée au handshake, des
> **ressources** par classe (Guerrier=Rage, Mage=Mana, Rôdeur=Énergie) et une **capacité raciale**
> unique par race (soin/attaque/défense/vitesse). Des monstres PvE data-driven aggrolent, poursuivent
> et attaquent. Le client de rendu Unity reste à venir (voir la [ROADMAP](docs/ROADMAP.md)).

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

# Terminal 2 — un guerrier orc qui chasse et tue les monstres proches
dotnet run --project src/Aetheria.Client.TestHarness -c Release -- --name Thakk --race 2 --class 1 --attack --seconds 20

# Terminal 3 — un second joueur qui se déplace, pour voir l'AoI en action
dotnet run --project src/Aetheria.Client.TestHarness -c Release -- --name Aria --dirx 1 --seconds 20
```

Chaque client affiche, une fois par seconde, sa santé et les entités présentes dans sa zone
d'intérêt (dont les monstres), et les événements de combat reçus. Avec `--attack`, le client fonce
sur le monstre le plus proche et utilise sa capacité — tu le vois infliger des dégâts, tuer, et les
monstres réapparaître après leur délai de respawn.

### Options du client de test

| Option      | Signification                                      | Défaut      |
|-------------|----------------------------------------------------|-------------|
| `--host`    | Hôte du serveur                                    | `127.0.0.1` |
| `--port`    | Port UDP du serveur                                | `27015`     |
| `--name`    | Nom affiché, envoyé lors du handshake              | `tester`    |
| `--seconds` | Durée de connexion                                 | `8`         |
| `--dirx`    | Intention de déplacement en X (-1..1)              | `0`         |
| `--diry`    | Intention de déplacement en Y (-1..1)              | `0`         |
| `--race`    | Id de race (1=Human, 4=Dwarf, 2=Orc, 3=Elf)        | `1`         |
| `--class`   | Id de classe (1=Warrior, 2=Mage, 3=Ranger)         | `1`         |
| `--gender`  | `male` / `female`                                  | `male`      |
| `--ability` | Id de capacité utilisée en `--attack`              | `1`         |
| `--attack`  | Chasse et attaque le monstre visible le plus proche| (désactivé) |
| `--racial`  | Utilise la capacité raciale (le serveur gère le CD)| (désactivé) |
| `--loot`    | Loote le cadavre visible le plus proche (à portée) | (désactivé) |
| `--account` | Identifiant de compte (banque persistante)         | = nom       |
| `--deposit` | Dépose N or en banque après connexion              | `0`         |
| `--instance`| Entre dans l'instance N (1=donjon, 2=raid 6-40)    | `0`         |

Rappel matrice classe/race : Humain→Guerrier/Mage · Nain→Guerrier/Rôdeur · Orc→Guerrier/Rôdeur ·
Elfe→Mage/Rôdeur. Une combinaison interdite est refusée à la connexion.

Le contenu (races, classes, capacités, monstres) est **data-driven** : voir les fichiers JSON dans
`src/Aetheria.Server/data/`, éditables sans recompiler.

## Structure du projet

```
Aetheria.slnx
├── src/
│   ├── Aetheria.Shared/            # code partagé serveur & client
│   │   ├── Math/                   #   Vec2 (vecteur sur le plan du monde)
│   │   ├── Spatial/                #   SpatialGrid — interest management
│   │   ├── Combat/                 #   StatBlock, modificateurs de race
│   │   ├── Data/                   #   définitions (race/classe/capacité/monstre) + registre GameData
│   │   ├── Protocol/               #   format réseau : PacketReader/Writer, messages
│   │   └── Net/                    #   abstraction ITransport + implémentation UDP brute
│   ├── Aetheria.Server/            # serveur autoritaire headless
│   │   ├── World/                  #   World (combat, IA, respawn), ServerEntity (source de vérité)
│   │   ├── data/                   #   contenu JSON (races, classes, capacités, monstres)
│   │   ├── GameServer.cs           #   liaison réseau <-> monde, handshake, snapshots, événements
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
