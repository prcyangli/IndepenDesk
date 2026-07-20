# IndepenDesk

[English](README.md) | [Türkçe](README.tr.md) | [Deutsch](README.de.md) | **Français** | [Italiano](README.it.md) | [Русский](README.ru.md) | [中文](README.zh-CN.md) | [日本語](README.ja.md)

**Bureaux virtuels indépendants par moniteur pour Windows** — l'équivalent Windows de « Les moniteurs disposent d'espaces distincts » de macOS.

## Le problème

Les bureaux virtuels de Windows sont globaux : `Win+Ctrl+←/→` change **tous les moniteurs à la fois**. Sous macOS, chaque écran a ses propres Spaces et seul l'écran sous le curseur bascule. IndepenDesk apporte ce comportement à Windows.

## Fonctionnalités

- 🖥 **Bureaux par moniteur** — le changement n'affecte que le moniteur sous la souris ; les autres restent intacts.
- ➕ **Nombre de bureaux dynamique et indépendant** — chaque moniteur démarre avec 1 bureau ; un balayage vers la droite en fin de liste en crée un nouveau (jusqu'à 9 par moniteur). Les bureaux vides en fin de liste sont supprimés automatiquement.
- 🔢 **Numérotation globale** — les numéros continuent d'un moniteur à l'autre (Moniteur 1 : 1-2-3, Moniteur 2 : 4-5-6). `Ctrl+Alt+chiffre` y accède directement.
- 🎞 **Animation de glissement façon macOS**, limitée au moniteur.
- 🗂 **Vue d'ensemble** (`Ctrl+Alt+↑`) — grille façon Mission Control avec glisser-déposer : déplacez les fenêtres entre bureaux et moniteurs, déplacez un bureau entier vers un autre moniteur, menu contextuel de déplacement.
- 🌍 **8 langues** — détection automatique, modifiable depuis le menu de la zone de notification.
- 🔄 **Vérification des mises à jour** via GitHub Releases.
- 🛟 **Résistant aux plantages** — les fenêtres masquées sont journalisées sur disque et restaurées au démarrage suivant.

## Installation

1. Installez le [runtime .NET 8 Desktop](https://dotnet.microsoft.com/download/dotnet/8.0) si nécessaire.
2. Téléchargez `IndepenDesk-vX.Y.Z-win-x64.zip` depuis les [Releases](https://github.com/harungecit/IndepenDesk/releases), extrayez et lancez `IndepenDesk.exe`.
3. L'application réside dans la zone de notification. Démarrage automatique : `Win+R` → `shell:startup` → déposez-y un raccourci.

## Raccourcis

| Raccourci | Action |
|---|---|
| `Ctrl+Alt+←` / `Ctrl+Alt+→` | Bureau précédent / suivant sur le moniteur sous la souris (à la fin, `→` en crée un nouveau) |
| `Ctrl+Alt+↑` | Ouvrir/fermer la vue d'ensemble |
| `Ctrl+Alt+1..9` | Aller au bureau portant ce numéro global |
| `Ctrl+Alt+Maj+←/→` | Déplacer la fenêtre active vers le bureau adjacent |

Un indicateur s'affiche à chaque changement (« Bureau 4 — Moniteur 2 • 2/3 »). Le menu contient un **Mode d'emploi** avec un guide animé des gestes.

## Pavé tactile (balayages façon macOS)

Par défaut, le balayage à quatre doigts déclenche le changement **global** de Windows. Pour le remplacer :

1. Ouvrez **Paramètres → Bluetooth et appareils → Pavé tactile → Mouvements avancés**.
2. Pour les balayages à quatre doigts, choisissez **Raccourci personnalisé** et enregistrez :
   - vers la gauche → `Ctrl+Alt+←`, vers la droite → `Ctrl+Alt+→`, vers le haut → `Ctrl+Alt+↑`
3. Le raccourci personnalisé remplace automatiquement le comportement par défaut — seul le moniteur sous le curseur bascule.

## Fonctionnement

IndepenDesk n'utilise pas le système global de Windows. Il gère des ensembles de fenêtres par moniteur et, lors d'un changement, masque/affiche uniquement les fenêtres de ce moniteur (`ShowWindow`). Les fenêtres masquées disparaissent aussi de la barre des tâches et d'Alt-Tab. Les nouvelles fenêtres sont rattachées au bureau actif de leur moniteur.

## Limitations connues

- Les fenêtres des applications élevées (admin) ne peuvent pas être masquées, sauf si IndepenDesk est lui-même lancé en administrateur.
- Le raccourci natif `Win+Ctrl+←/→` déclenche toujours le changement global — il suffit de ne pas l'utiliser.
- `Ctrl+Alt+←/→` peut entrer en conflit avec les raccourcis Intel « rotation de l'écran » ; désactivez-les si besoin.
- Le menu contextuel de la barre des tâches de Windows 11 n'est pas extensible ; utilisez le menu contextuel de la vue d'ensemble.

## Compilation

```
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true -o publish
```

Nécessite le SDK .NET 8. Les releases sont générées automatiquement par GitHub Actions lors du push d'un tag `v*`.

## Licence

[MIT](LICENSE)
