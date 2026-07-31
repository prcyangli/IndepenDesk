# Dağıtım kanalları rehberi

Yeni sürüm çıkarma: `IndepenDesk.csproj` içindeki `<Version>` artırılır → commit →
`git tag vX.Y.Z && git push origin vX.Y.Z`. Release workflow'u tüm paketleri
(zip, Inno Setup exe, MSI, MSIX) üretip GitHub Releases'a yükler. Sonrasında
kanallar aşağıdaki gibi güncellenir.

## winget (yayında)

Paket kimliği: `harungecit.IndepenDesk` — Inno Setup exe'lerine işaret eder.

Her yeni sürümde tek komut:

```powershell
wingetcreate update harungecit.IndepenDesk --version X.Y.Z --urls `
  "https://github.com/harungecit/IndepenDesk/releases/download/vX.Y.Z/IndepenDesk-Setup-X.Y.Z-x64.exe" `
  "https://github.com/harungecit/IndepenDesk/releases/download/vX.Y.Z/IndepenDesk-Setup-X.Y.Z-x86.exe" `
  "https://github.com/harungecit/IndepenDesk/releases/download/vX.Y.Z/IndepenDesk-Setup-X.Y.Z-arm64.exe" `
  --token (gh auth token) --submit
```

Komut, microsoft/winget-pkgs'e PR açar; moderasyon genellikle birkaç gün sürer.

## Scoop (başvuruldu — Extras bucket)

Manifest: `ScoopInstaller/Extras` deposunda `bucket/independesk.json`; taşınabilir
zip'lere işaret eder. İlk başvuru PR'ı: https://github.com/ScoopInstaller/Extras/pull/18426

Manifest'te `checkver: github` + `autoupdate` tanımlı olduğundan **yeni sürümlerde
elle işlem gerekmez**: Scoop'un Excavator botu GitHub Releases'ı görüp manifesti
kendiliğinden günceller. Tek koşul, release varlık adlarının aynı şablonda kalması:
`IndepenDesk-vX.Y.Z-win-<arch>.zip`. Şablon değişirse Extras'a manifest düzeltme
PR'ı gerekir.

Kurulum: `scoop bucket add extras` sonrası `scoop install independesk`.

## Chocolatey (hesap gerekiyor — paket iskeleti hazır)

Paket iskeleti bu depoda: `packaging/choco/` (nuspec + chocolateyinstall.ps1,
Inno Setup exe'lerini sessiz kurar; kaldırma, Chocolatey'nin AutoUninstaller'ı
ile otomatik). Yayınlamak için bir defalık hesap kurulumu şart:

1. **Hesap:** https://community.chocolatey.org adresinde hesap açın ve profil
   sayfanızdan API anahtarınızı kopyalayın.
2. **choco CLI kurulumu** (yönetici PowerShell):
   ```powershell
   Set-ExecutionPolicy Bypass -Scope Process -Force
   iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
   ```
3. **API anahtarını kaydedin** (bir defa):
   ```powershell
   choco apikey add --source https://push.chocolatey.org/ --key API_ANAHTARINIZ
   ```
4. **Paketle ve gönder:**
   ```powershell
   cd packaging\choco
   choco pack
   choco push independesk.0.4.0.nupkg --source https://push.chocolatey.org/
   ```
5. **Moderasyon:** İlk paket otomatik doğrulayıcıdan (paketi sanal makinede
   kurar) ve insan moderatörden geçer; genellikle birkaç gün ile birkaç hafta
   sürer. E-posta ile bilgilendirilirsiniz. Onaylanınca
   `choco install independesk` çalışır.

**Yeni sürümde:** `independesk.nuspec` içindeki `<version>` ve `<releaseNotes>`,
`tools/chocolateyinstall.ps1` içindeki URL'ler ve sha256 özetleri güncellenir
(özetler `gh release view vX.Y.Z --json assets -q '.assets[].digest'` ile
alınabilir), sonra yine `choco pack` + `choco push`. Sonraki sürümler ilk
onaydan sonra çok daha hızlı moderasyondan geçer.
