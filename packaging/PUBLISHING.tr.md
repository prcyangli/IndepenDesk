# Dağıtım kanalları rehberi

Yeni sürüm çıkarma: `IndepenDesk.csproj` içindeki `<Version>` artırılır → commit →
`git tag vX.Y.Z && git push origin vX.Y.Z`. Release workflow'u zip, Inno Setup
exe ve MSI paketlerini üretip GitHub Releases'a yükler. `AppxManifest.xml`,
sertifika ve MSIX görselleri şu anda eski, etkin olmayan varlıklardır; workflow
MSIX üretmez. Sonrasında
kanallar aşağıdaki gibi güncellenir.

## winget (fork paketi yayımlandıktan sonra)

Fork için kullanılacak paket kimliği: `prcyangli.IndepenDesk` — Inno Setup exe'lerine
işaret eder. Bu kimlik winget'te yayımlanmadan README'ye kurulum komutu eklenmemelidir;
eski upstream kimliği farklı depo kodunu kurar.

Her yeni sürümde tek komut:

```powershell
wingetcreate update prcyangli.IndepenDesk --version X.Y.Z --urls `
  "https://github.com/prcyangli/IndepenDesk/releases/download/vX.Y.Z/IndepenDesk-Setup-X.Y.Z-x64.exe" `
  "https://github.com/prcyangli/IndepenDesk/releases/download/vX.Y.Z/IndepenDesk-Setup-X.Y.Z-x86.exe" `
  "https://github.com/prcyangli/IndepenDesk/releases/download/vX.Y.Z/IndepenDesk-Setup-X.Y.Z-arm64.exe" `
  --token (gh auth token) --submit
```

Komut, microsoft/winget-pkgs'e PR açar; moderasyon genellikle birkaç gün sürer.

## Scoop (fork kaydı yayımlanmadan kullanmayın)

`ScoopInstaller/Extras` içindeki mevcut `independesk` manifestinin indirme ve
`autoupdate` URL'leri önce `prcyangli/IndepenDesk` Releases'a geçirilmelidir.
Bu değişiklik onaylanana kadar README'de Scoop kurulumu önerilmemelidir.

Manifest'te `checkver: github` + `autoupdate` tanımlı olduğundan **yeni sürümlerde
elle işlem gerekmez**: Scoop'un Excavator botu GitHub Releases'ı görüp manifesti
kendiliğinden günceller. Tek koşul, release varlık adlarının aynı şablonda kalması:
`IndepenDesk-vX.Y.Z-win-<arch>.zip`. Şablon değişirse Extras'a manifest düzeltme
PR'ı gerekir.

Onaylandıktan sonra kurulum: `scoop bucket add extras` ve ardından
`scoop install independesk`.

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
   choco push independesk.0.4.1.nupkg --source https://push.chocolatey.org/
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
