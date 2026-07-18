# Client Unity — AetheriaClient

Le client de rendu isométrique 3D. Il consomme `Aetheria.Shared.dll` (la couche protocole/réseau du
serveur compilée en netstandard2.1, déjà copiée dans `Assets/Plugins/`), donc **le format réseau ne
peut pas diverger** entre Unity et le serveur : c'est littéralement le même code.

## Démarrage (5 minutes)

1. Installe **Unity 2022.3 LTS ou plus récent** (Unity 6 fonctionne) via Unity Hub.
2. Dans Unity Hub : **Add → Add project from disk** → sélectionne le dossier `unity/AetheriaClient`.
   Si ta version d'Unity diffère de celle du fichier `ProjectVersion.txt`, accepte la mise à niveau.
3. Ouvre le projet. Aucune scène à configurer : appuie simplement sur **Play** dans la scène vide —
   le bootstrap construit tout (caméra iso, sol, lumière, client réseau).
4. Lance le serveur à côté :
   ```bash
   dotnet run --project src/Aetheria.Server -c Release
   ```
5. Dans le jeu : choisis nom / compte / race / classe / genre → **JOUER**.

## Contrôles

| Touche | Action |
|--------|--------|
| WASD / flèches | Se déplacer (relatif à la caméra) |
| Tab | Cibler l'ennemi suivant (monstres + joueurs adverses) |
| 1 | Attaque de base sur la cible |
| 2 | Sort avancé (débloqué niveau 3) |
| R | Capacité raciale |
| F | Looter le cadavre le plus proche |
| G / H / J | Inviter la cible / accepter l'invitation / quitter le groupe |
| I / O / L | Entrer donjon (instance 1) / raid (instance 2) / sortir |
| B / N | Déposer / retirer 10 or en banque |
| Molette | Zoom |
| Échap | Se déconnecter |

Code couleur : **vert** = toi · **bleu** = allié · **rouge** = faction adverse (PvP !) · **orange** =
monstre · **violet (gros)** = boss · **gris plat** = cadavre lootable.

## Après une modification de `src/Aetheria.Shared`

La DLL doit être resynchronisée :

```powershell
# Windows
powershell -ExecutionPolicy Bypass -File tools/sync-unity-shared.ps1
```

```bash
# macOS / Linux
./tools/sync-unity-shared.sh
```

## Notes d'architecture

- Le serveur reste 100 % autoritaire : ce client n'envoie que des *intentions* (déplacement, sorts)
  et interpole les snapshots 20 Hz reçus (voir `EntityView`).
- Le plan serveur (X, Y) est mappé sur le plan sol Unity (X, Z).
- Le HUD est en OnGUI (zéro dépendance de package) — il sera remplacé par une vraie UI plus tard.
- Prochaines étapes client (roadmap M1) : prédiction locale du déplacement + réconciliation,
  interpolation à retard fixe, et vrais modèles/animations à la place des primitives.
