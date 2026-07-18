# Personnages Mixamo — mode d'emploi

Dépose ici les FBX téléchargés depuis [mixamo.com](https://www.mixamo.com) (compte Adobe gratuit),
puis dans Unity lance le menu **Aetheria → Configurer les modèles externes (Mixamo)**.
Le jeu remplacera alors automatiquement les personnages procéduraux par tes personnages
animés — et retombera sur les procéduraux si ce dossier est vide.

## 1. Le personnage (une seule fois)

1. Onglet **Characters** → choisis un personnage (ex. *Knight D Pelegrini*, *Paladin W Prop*,
   *Erika Archer* — style fantasy conseillé).
2. Bouton **Download** :
   - Format : **FBX for Unity (.fbx)**
   - Pose : **T-pose**
3. Renomme le fichier selon la race qu'il incarnera puis dépose-le ici :
   - `Character.fbx` → utilisé par TOUT le monde (le plus simple pour commencer)
   - ou par race/sexe : `Human_Male.fbx`, `Human_Female.fbx`, `Orc_Male.fbx`,
     `Elf_Female.fbx`, `Dwarf_Male.fbx`, …

## 2. Les animations (avec le même personnage affiché)

Cherche puis télécharge chacune (bouton **Download**) :

| Recherche Mixamo        | Réglages                                   | Nom de fichier conseillé |
|-------------------------|--------------------------------------------|--------------------------|
| `Idle`                  | FBX for Unity, **Without Skin**            | `Anim_Idle.fbx`          |
| `Walking` (ou `Run`)    | FBX for Unity, **Without Skin**, In Place ✔ | `Anim_Walk.fbx`          |
| `Sword Slash` (ou autre attaque) | FBX for Unity, **Without Skin**   | `Anim_Attack.fbx`        |
| `Jump`                  | FBX for Unity, **Without Skin**, In Place ✔ | `Anim_Jump.fbx`          |

> **Important** : coche **In Place** quand l'option existe (le serveur gère le déplacement),
> et prends *Without Skin* pour les animations (fichiers plus légers).
> Les noms de fichiers doivent contenir `idle`, `walk`/`run`, `attack`/`slash`, `jump` —
> c'est ainsi que le configurateur les reconnaît.

## 3. Dans Unity

1. Copie les FBX dans ce dossier (`Assets/Resources/ExternalModels/`).
2. Menu **Aetheria → Configurer les modèles externes (Mixamo)** — il règle les rigs en
   Humanoid, met les cycles en boucle et génère `CharacterAnimator.controller`.
3. Play : les joueurs utilisent le personnage Mixamo (vitesse, attaques et sauts réseau
   pilotent les animations). Monstres et PNJ restent procéduraux pour l'instant.
