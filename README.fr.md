# IndepenDesk

[English](README.md) | [Türkçe](README.tr.md) | [Deutsch](README.de.md) | **Français** | [Italiano](README.it.md) | [Русский](README.ru.md) | [中文](README.zh-CN.md) | [日本語](README.ja.md)

**Bureaux virtuels indépendants par moniteur pour Windows** — l'équivalent Windows de « Les moniteurs disposent d'espaces distincts » de macOS.

## Le problème

Les bureaux virtuels de Windows sont globaux : `Win+Ctrl+←/→` change **tous les moniteurs à la fois**. Sous macOS, chaque écran a ses propres Spaces et seul l'écran sous le curseur bascule. IndepenDesk apporte ce comportement à Windows.

## Fonctionnalités

- 🖥 **Bureaux par moniteur** — le changement n'affecte que le moniteur sous la souris ; les autres restent intacts.
- ➕ **Nombre de bureaux dynamique et indépendant** — chaque moniteur démarre avec 1 bureau ; aller à droite en fin de liste en crée un nouveau (jusqu'à 9 par moniteur). Les bureaux inutilisés créés ainsi sont supprimés après les avoir quittés ; les bureaux vides ajoutés dans la vue d'ensemble restent jusqu'à leur fermeture manuelle.
- 🔢 **Raccourcis numériques globaux** — `Ctrl+Alt+chiffre` navigue entre moniteurs dans l'ordre des écrans ; la vue d'ensemble et l'OSD utilisent une numérotation locale par moniteur.
- 🔔 **Indicateur compact à l'écran** après chaque changement, sans animation susceptible de prendre le focus.
- 🗂 **Vue d'ensemble** (`Ctrl+Alt+↑`) — grille façon Mission Control avec glisser-déposer : déplacez les fenêtres entre bureaux et moniteurs, déplacez un bureau entier vers un autre moniteur, menu contextuel de déplacement.
- 📌 **Barre des tâches partagée** (optionnel, menu de notification) — les fenêtres de tous les bureaux restent dans la barre des tâches et Alt-Tab ; un clic saute directement à son bureau.
- 🌍 **8 langues** — détection automatique, modifiable depuis le menu de la zone de notification.
- 🔄 **Vérification des mises à jour** via GitHub Releases.
- 🚀 **Démarre avec Windows** par défaut — désactivable à tout moment depuis le menu de la zone de notification.
- 🛟 **Résistant aux plantages** — les fenêtres masquées sont journalisées sur disque et restaurées au démarrage suivant.

## Installation

Fonctionne sous Windows 10 (1607+) et Windows 11 sur **x64, x86 et ARM64**. Tous les paquets sont autonomes — aucun runtime .NET requis.

Les fiches de ce fork ne sont pas encore publiées dans les gestionnaires de paquets. D'ici là, utilisez uniquement les Releases `prcyangli` ci-dessous ; l'ancien paquet winget upstream installe un autre code.

**Installateur (recommandé) :** téléchargez `IndepenDesk-Setup-<version>-<arch>.exe` depuis les [Releases](https://github.com/prcyangli/IndepenDesk/releases) et exécutez-le — icône de bureau en option, 7 langues d'installation.

**MSI** (déploiement d'entreprise / GPO) : `IndepenDesk-<version>-<arch>.msi`.

**Portable :** `IndepenDesk-v<version>-win-<arch>.zip` — extrayez et lancez, rien à installer.


L'application réside dans la zone de notification (icône aux deux écrans bleus).

## Raccourcis

| Raccourci | Action |
|---|---|
| `Ctrl+Alt+←` / `Ctrl+Alt+→` | Bureau précédent / suivant sur le moniteur sous la souris (à la fin, `→` en crée un nouveau) |
| `Ctrl+Alt+↑` | Ouvrir/fermer la vue d'ensemble |
| `Ctrl+Alt+1..9` | Aller au bureau portant ce numéro global |
| `Ctrl+Alt+Maj+←/→` | Déplacer la fenêtre active vers le bureau adjacent |

Un indicateur s'affiche à chaque changement (par exemple « Bureau 2 »). Le menu contient un **Mode d'emploi** avec un guide animé des gestes.

## Pavé tactile (balayages façon macOS)

Par défaut, le balayage à quatre doigts déclenche le changement **global** de Windows. Pour le remplacer :

1. Ouvrez **Paramètres → Bluetooth et appareils → Pavé tactile → Mouvements avancés**.
2. Pour les balayages à quatre doigts, choisissez **Raccourci personnalisé** et enregistrez :
   - vers la gauche → `Ctrl+Alt+←`, vers la droite → `Ctrl+Alt+→`, vers le haut → `Ctrl+Alt+↑`
3. Le raccourci personnalisé remplace automatiquement le comportement par défaut — seul le moniteur sous le curseur bascule.

## Fonctionnement

IndepenDesk n'utilise pas le système global de Windows. Il gère des ensembles de fenêtres par moniteur et, lors d'un changement, masque/affiche uniquement les fenêtres de ce moniteur (`ShowWindow`). Les fenêtres masquées disparaissent aussi de la barre des tâches et d'Alt-Tab. Les nouvelles fenêtres sont rattachées au bureau actif de leur moniteur.

Le mode optionnel **barre des tâches partagée** (menu de notification) parque les fenêtres des bureaux inactifs hors écran au lieu de les masquer. Les fenêtres de tous les bureaux restent alors dans la barre des tâches et Alt-Tab, et en activer une bascule vers son bureau. Les positions parquées sont journalisées comme les fenêtres masquées et restaurées au démarrage suivant un plantage.

## Limitations connues

- Les fenêtres des applications élevées (admin) ne peuvent pas être masquées, sauf si IndepenDesk est lui-même lancé en administrateur.
- En mode barre des tâches partagée, les fenêtres parquées continuent de se dessiner (légère hausse GPU/CPU par rapport au masquage). Si le processus est tué dans ce mode, relancez IndepenDesk 0.4.3+ pour ramener les fenêtres parquées à l'écran (les versions antérieures ne lisent pas le nouveau format de journal).
- Le raccourci natif `Win+Ctrl+←/→` déclenche toujours le changement global — il suffit de ne pas l'utiliser.
- `Ctrl+Alt+←/→` peut entrer en conflit avec les raccourcis Intel « rotation de l'écran » ; désactivez-les si besoin.
- Le menu contextuel de la barre des tâches de Windows 11 n'est pas extensible ; utilisez le menu contextuel de la vue d'ensemble.

## Licence

[MIT](LICENSE)
