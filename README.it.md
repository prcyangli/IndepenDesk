# IndepenDesk

[English](README.md) | [Türkçe](README.tr.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | **Italiano** | [Русский](README.ru.md) | [中文](README.zh-CN.md) | [日本語](README.ja.md)

**Desktop virtuali indipendenti per ogni monitor su Windows** — l'equivalente Windows di "Le scrivanie hanno spazi separati" di macOS.

## Il problema

I desktop virtuali di Windows sono globali: `Win+Ctrl+←/→` cambia **tutti i monitor contemporaneamente**. Su macOS ogni schermo ha i propri Spaces e cambia solo lo schermo sotto il cursore. IndepenDesk porta questo comportamento su Windows.

## Funzionalità

- 🖥 **Desktop per monitor** — il cambio riguarda solo il monitor sotto il mouse; gli altri restano intatti.
- ➕ **Numero di desktop dinamico e indipendente** — ogni monitor parte con 1 desktop; scorrendo a destra alla fine se ne crea uno nuovo (fino a 9 per monitor). I desktop vuoti in coda vengono rimossi automaticamente.
- 🔢 **Numerazione globale** — i numeri proseguono tra i monitor (Monitor 1: 1-2-3, Monitor 2: 4-5-6). `Ctrl+Alt+cifra` porta direttamente a quel desktop.
- 🎞 **Animazione di scorrimento in stile macOS**, limitata al monitor.
- 🗂 **Panoramica** (`Ctrl+Alt+↑`) — griglia in stile Mission Control con trascinamento: sposta le finestre tra desktop e monitor, sposta interi desktop su un altro monitor, menu contestuale di spostamento.
- 🌍 **8 lingue** — rilevate automaticamente, modificabili dal menu nella barra di sistema.
- 🔄 **Controllo aggiornamenti** tramite GitHub Releases.
- 🚀 **Si avvia con Windows** per impostazione predefinita — disattivabile in qualsiasi momento dal menu nella barra di sistema.
- 🛟 **A prova di crash** — le finestre nascoste vengono registrate su disco e ripristinate al riavvio.

## Installazione

Funziona su Windows 10 (1607+) e Windows 11 su **x64, x86 e ARM64**. Tutti i pacchetti sono autonomi — non serve il runtime .NET.

**winget:**

```
winget install harungecit.IndepenDesk
```

**Installer (consigliato):** scarica `IndepenDesk-Setup-<versione>-<arch>.exe` dalle [Releases](https://github.com/prcyangli/IndepenDesk/releases) ed eseguilo — con icona desktop opzionale, in 7 lingue.

**MSI** (distribuzione aziendale / GPO): `IndepenDesk-<versione>-<arch>.msi`.

**Portatile:** `IndepenDesk-v<versione>-win-<arch>.zip` — estrai ed esegui, nessuna installazione.


L'app risiede nella barra di sistema (icona con due schermi blu).

## Scorciatoie

| Scorciatoia | Azione |
|---|---|
| `Ctrl+Alt+←` / `Ctrl+Alt+→` | Desktop precedente / successivo sul monitor sotto il mouse (alla fine, `→` ne crea uno nuovo) |
| `Ctrl+Alt+↑` | Apri/chiudi la panoramica |
| `Ctrl+Alt+1..9` | Vai al desktop con quel numero globale |
| `Ctrl+Alt+Maiusc+←/→` | Sposta la finestra attiva sul desktop adiacente |

A ogni cambio appare un indicatore ("Desktop 4 — Monitor 2 • 2/3"). Il menu contiene **Come si usa…** con una guida animata dei gesti.

## Touchpad (scorrimenti in stile macOS)

Per impostazione predefinita lo scorrimento a quattro dita attiva il cambio **globale** di Windows. Per sostituirlo:

1. Apri **Impostazioni → Bluetooth e dispositivi → Touchpad → Movimenti avanzati**.
2. Per gli scorrimenti a quattro dita scegli **Collegamento personalizzato** e registra:
   - a sinistra → `Ctrl+Alt+←`, a destra → `Ctrl+Alt+→`, in alto → `Ctrl+Alt+↑`
3. Il collegamento personalizzato sostituisce automaticamente il comportamento predefinito — cambia solo il monitor sotto il cursore.

## Come funziona

IndepenDesk non usa il sistema globale di Windows. Gestisce insiemi di finestre per monitor e, al cambio, nasconde/mostra solo le finestre di quel monitor (`ShowWindow`). Le finestre nascoste spariscono anche dalla barra delle applicazioni e da Alt-Tab. Le nuove finestre vengono assegnate al desktop attivo del loro monitor.

## Limitazioni note

- Le finestre delle app con privilegi elevati non possono essere nascoste, a meno che IndepenDesk non sia eseguito come amministratore.
- Il nativo `Win+Ctrl+←/→` attiva ancora il cambio globale — basta non usarlo.
- `Ctrl+Alt+←/→` può entrare in conflitto con le scorciatoie Intel "ruota schermo"; disattivale se necessario.
- Il menu contestuale della barra delle applicazioni di Windows 11 non è estendibile; usa il menu della panoramica.

## Licenza

[MIT](LICENSE)
