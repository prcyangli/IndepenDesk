# IndepenDesk

[English](README.md) | [Türkçe](README.tr.md) | **Deutsch** | [Français](README.fr.md) | [Italiano](README.it.md) | [Русский](README.ru.md) | [中文](README.zh-CN.md) | [日本語](README.ja.md)

**Unabhängige virtuelle Desktops pro Monitor für Windows** — das Windows-Gegenstück zu macOS „Displays have separate Spaces".

## Das Problem

Virtuelle Desktops unter Windows sind global: `Win+Strg+←/→` wechselt **alle Monitore gleichzeitig**. Unter macOS hat jeder Bildschirm eigene Spaces, und nur der Bildschirm unter dem Cursor wechselt. IndepenDesk bringt dieses Verhalten auf Windows.

## Funktionen

- 🖥 **Desktops pro Monitor** — ein Wechsel betrifft nur den Monitor unter der Maus; alle anderen bleiben unberührt.
- ➕ **Dynamische, unabhängige Desktop-Anzahl** — jeder Monitor startet mit 1 Desktop; rechts am Ende wird ein neuer erstellt (max. 9 pro Monitor). So automatisch erstellte, unbenutzte Desktops werden nach dem Verlassen entfernt; in der Übersicht angelegte leere Desktops bleiben bis zum manuellen Schließen erhalten.
- 🔢 **Globale Ziffernkürzel** — `Strg+Alt+Ziffer` springt monitorübergreifend: zuerst das eingebaute Display, danach externe Bildschirme in Bildschirmreihenfolge; in der Übersicht hat das eingebaute Display immer die Nummer 1 und steht ganz unten.
- 🔔 **Kompakte Bildschirmanzeige** nach jedem Wechsel, ohne fokusraubende Wischanimation.
- 🗂 **Übersicht** (`Strg+Alt+↑`) — Mission-Control-ähnliche Ansicht mit Drag & Drop: Fenster zwischen Desktops und Monitoren verschieben, ganze Desktops auf einen anderen Monitor ziehen, Rechtsklick-Menü.
- 📌 **Gemeinsame Taskleiste** (optional, Tray-Menü) — Fenster aller Desktops bleiben in Taskleiste und Alt-Tab; ein Klick springt direkt zum zugehörigen Desktop.
- 🌍 **8 Sprachen** — automatisch erkannt, über das Tray-Menü änderbar.
- 🔄 **Update-Prüfung** über GitHub Releases im Tray-Menü.
- 🚀 **Startet standardmäßig mit Windows** — jederzeit im Tray-Menü abschaltbar.
- 🛟 **Absturzsicher** — versteckte Fenster werden protokolliert und beim nächsten Start wiederhergestellt.

## Installation

Läuft unter Windows 10 (1607+) und Windows 11 auf **x64, x86 und ARM64**. Alle Pakete sind eigenständig — keine .NET-Laufzeit erforderlich.

Die Paketmanager-Einträge dieses Forks sind noch nicht veröffentlicht. Verwenden Sie bis dahin ausschließlich die unten verlinkten Releases von `prcyangli`; das frühere upstream-winget-Paket installiert anderen Code.

**Installer (empfohlen):** `IndepenDesk-Setup-<Version>-<Arch>.exe` von den [Releases](https://github.com/prcyangli/IndepenDesk/releases) herunterladen und ausführen — mit optionalem Desktop-Symbol, in 7 Sprachen.

**MSI** (Unternehmens-/GPO-Bereitstellung): `IndepenDesk-<Version>-<Arch>.msi`.

**Portabel:** `IndepenDesk-v<Version>-win-<Arch>.zip` — entpacken und starten, keine Installation.


Die App sitzt im Infobereich (Symbol mit zwei blauen Bildschirmen).

## Tastenkürzel

| Kürzel | Aktion |
|---|---|
| `Strg+Alt+←` / `Strg+Alt+→` | Vorheriger / nächster Desktop auf dem Monitor unter der Maus (am Ende erstellt `→` einen neuen) |
| `Strg+Alt+↑` | Übersicht öffnen/schließen |
| `Strg+Alt+1..9` | Zum Desktop mit dieser globalen Nummer springen |
| `Strg+Alt+Umschalt+←/→` | Aktives Fenster auf den Nachbar-Desktop verschieben |

Bei jedem Wechsel erscheint eine Anzeige (zum Beispiel „Desktop 2“). Das Tray-Menü enthält eine **Bedienungsanleitung** mit animierter Gestenübersicht.

## Touchpad (Wischgesten wie bei macOS)

Standardmäßig löst das Vier-Finger-Wischen den **globalen** Windows-Wechsel aus. So überschreiben Sie es:

1. **Einstellungen → Bluetooth & Geräte → Touchpad → Erweiterte Gesten** öffnen.
2. Für die Vier-Finger-Wischgesten **Benutzerdefinierte Tastenkombination** wählen und aufzeichnen:
   - links → `Strg+Alt+←`, rechts → `Strg+Alt+→`, oben → `Strg+Alt+↑`
3. Die benutzerdefinierte Kombination ersetzt automatisch den Windows-Standard — nur der Monitor unter dem Cursor wechselt.

## Funktionsweise

IndepenDesk nutzt nicht das globale System von Windows. Stattdessen verwaltet es Fenstermengen pro Monitor und blendet beim Wechsel nur die Fenster dieses Monitors aus/ein (`ShowWindow`). Versteckte Fenster verschwinden auch aus Taskleiste und Alt-Tab. Neue Fenster werden dem aktiven Desktop ihres Monitors zugeordnet.

Der optionale Modus **Gemeinsame Taskleiste** (Tray-Menü) parkt Fenster inaktiver Desktops, indem er sie an Ort und Stelle minimiert, statt sie auszublenden. Damit bleiben die Fenster aller Desktops in Taskleiste und Alt-Tab sichtbar, und das Aktivieren eines Fensters wechselt zu seinem Desktop. Geparkte Positionen werden wie versteckte protokolliert und nach einem Absturz beim nächsten Start wiederhergestellt.

## Bekannte Einschränkungen

- Fenster von Programmen mit Administratorrechten können nur versteckt werden, wenn IndepenDesk selbst als Administrator läuft.
- „Benutzerdefinierte" Touchpad-Gesten sind synthetische Tastendrücke: Solange ein Fenster mit Administratorrechten fokussiert ist, verwirft die Windows-Sicherheit (UIPI) sie, und die Gesten wirken nicht mehr. Physische Tastenkürzel und das Tray-Menü funktionieren weiterhin — nutzen Sie dann diese.
- Im Modus „Gemeinsame Taskleiste“ werden geparkte Fenster an Ort und Stelle minimiert (das Rendering stoppt). Wird der Prozess in diesem Modus abgeschossen, bleiben sie einfach minimiert: Stellen Sie sie über die Taskleiste wieder her, oder starten Sie IndepenDesk 0.4.4+ erneut, damit sie wieder verwaltet werden (ältere Versionen können das v3-Journalformat nicht lesen).
- Das native `Win+Strg+←/→` löst weiterhin den globalen Wechsel aus — einfach nicht verwenden.
- `Strg+Alt+←/→` kann mit Intel-Grafik-Hotkeys („Bildschirm drehen") kollidieren; ggf. in den Intel-Einstellungen deaktivieren.
- Das Kontextmenü der Windows-11-Taskleiste ist nicht erweiterbar; nutzen Sie das Rechtsklick-Menü der Übersicht.

## Lizenz

[MIT](LICENSE)
