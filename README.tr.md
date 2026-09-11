# IndepenDesk

[English](README.md) | **Türkçe** | [Deutsch](README.de.md) | [Français](README.fr.md) | [Italiano](README.it.md) | [Русский](README.ru.md) | [中文](README.zh-CN.md) | [日本語](README.ja.md)

**Windows'ta monitör başına bağımsız sanal masaüstleri** — macOS'taki "Displays have separate Spaces" davranışının Windows karşılığı.

## Sorun

Windows'un sanal masaüstleri globaldir: `Win+Ctrl+←/→` **tüm monitörleri birden** değiştirir. macOS'ta ise her ekranın kendi Space'leri vardır ve yalnızca imlecin olduğu ekran geçiş yapar. IndepenDesk bu davranışı Windows'a getirir.

## Özellikler

- 🖥 **Monitör başına masaüstleri** — geçiş yalnızca farenin olduğu monitörü etkiler; diğerlerine dokunulmaz.
- ➕ **Dinamik ve bağımsız masaüstü sayıları** — her monitör 1 masaüstüyle başlar; sonda sağa kaydırmak yenisini oluşturur (monitör başına en fazla 9). Boşalan sondaki masaüstleri otomatik silinir. Bir monitörde 5, diğerinde 2 masaüstü olabilir.
- 🔢 **Global numaralandırma** — numaralar monitörler arasında devam eder (Monitör 1: 1-2-3, Monitör 2: 4-5-6). `Ctrl+Alt+rakam` o masaüstü hangi monitördeyse oraya gider.
- 🎞 **macOS tarzı kaydırma animasyonu**, monitörün içine kırpılmış.
- 🗂 **Genel Bakış** (`Ctrl+Alt+↑`) — Mission Control benzeri ekran, sürükle-bırak: pencereleri masaüstleri ve monitörler arasında taşıyın, masaüstünü komple başka monitöre taşıyın, sağ tık taşıma menüsü.
- 🌍 **8 dil** — English, Türkçe, Deutsch, Français, Italiano, Русский, 中文, 日本語 (otomatik algılanır, tray menüsünden değiştirilebilir).
- 🔄 **Güncelleme denetimi** — tray menüsünden, GitHub Releases üzerinden.
- 🚀 **Windows ile birlikte başlar** (varsayılan) — tray menüsünden istediğiniz an kapatılabilir.
- 🛟 **Çökmeye dayanıklı** — gizlenen pencereler diske kaydedilir ve sonraki açılışta kurtarılır; çıkışta ve monitör çıkarıldığında her şey geri gelir.

## Kurulum

Windows 10 (1607+) ve Windows 11'de **x64, x86 ve ARM64** üzerinde çalışır. Tüm paketler kendi kendine yeterlidir — .NET kurulumu gerekmez.

**winget:**

```
winget install harungecit.IndepenDesk
```

**Kurulum sihirbazı (önerilen):** [Releases](https://github.com/prcyangli/IndepenDesk/releases) sayfasından `IndepenDesk-Setup-<sürüm>-<mimari>.exe` indirip çalıştırın — isteğe bağlı masaüstü simgesi, 7 kurulum dili.

**MSI** (kurumsal / GPO dağıtımı): `IndepenDesk-<sürüm>-<mimari>.msi`.

**Taşınabilir:** `IndepenDesk-v<sürüm>-win-<mimari>.zip` — açıp çalıştırın, kurulum gerekmez.


Uygulama sistem tepsisinde durur (iki mavi ekran simgesi).

## Kısayollar

| Kısayol | İşlev |
|---|---|
| `Ctrl+Alt+←` / `Ctrl+Alt+→` | Farenin olduğu monitörde önceki / sonraki masaüstü (sonda `→` yenisini oluşturur) |
| `Ctrl+Alt+↑` | Genel Bakışı aç/kapat |
| `Ctrl+Alt+1..9` | Global numaralı masaüstüne git |
| `Ctrl+Alt+Shift+←/→` | Aktif pencereyi bitişik masaüstüne taşı ve oraya geç |

Her geçişte ekranda gösterge belirir ("Masaüstü 4 — Monitör 2 • 2/3"). Tray menüsündeki **Nasıl kullanılır…** penceresinde animasyonlu hareket rehberi vardır.

## Touchpad (macOS tarzı kaydırma)

Varsayılanda 4 parmak kaydırma Windows'un **global** geçişini tetikler. Ezmek için:

1. **Ayarlar → Bluetooth ve cihazlar → Dokunmatik yüzey → Gelişmiş hareketler**'i açın.
2. Dört parmak kaydırmalar için **Özel kısayol** seçip kaydedin:
   - sola çekme → `Ctrl+Alt+←`, sağa çekme → `Ctrl+Alt+→`, yukarı çekme → `Ctrl+Alt+↑`
3. Özel kısayol kaydettiğiniz anda Windows varsayılanı otomatik devre dışı kalır — yalnızca imlecin olduğu monitör değişir.

## Nasıl çalışır

IndepenDesk, Windows'un global sanal masaüstü sistemini kullanmaz (o sistem düzeltilemez). Bunun yerine monitör başına pencere setleri tutar ve geçişte yalnızca o monitörün pencerelerini gizler/gösterir (`ShowWindow`). Gizlenen pencereler görev çubuğundan ve Alt-Tab'dan da kalkar — gerçek masaüstü geçişi hissi verir. Yeni pencereler açıldıkları monitörün aktif masaüstüne atanır; başka monitöre sürüklenen pencere otomatik oraya geçer.

## Bilinen sınırlar

- Yönetici yetkisiyle çalışan uygulamaların pencereleri, IndepenDesk yönetici değilse gizlenemez.
- Yerleşik `Win+Ctrl+←/→` hâlâ global geçişi tetikler — kullanmamak yeterli.
- `Ctrl+Alt+←/→` bazı Intel ekran sürücülerinde "ekranı döndür" ile çakışabilir; gerekirse Intel ayarlarından kapatın (kayıt başarısız olursa tray bildirimi görürsünüz).
- Windows 11 görev çubuğu menüsü üçüncü parti uygulamalarca genişletilemez; Genel Bakış'taki sağ tık menüsünü kullanın.

## Lisans

[MIT](LICENSE)
