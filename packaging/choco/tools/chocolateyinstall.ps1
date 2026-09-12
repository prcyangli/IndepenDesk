$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = 'independesk'
  fileType       = 'exe'
  url            = 'https://github.com/prcyangli/IndepenDesk/releases/download/v0.4.1/IndepenDesk-Setup-0.4.1-x86.exe'
  url64bit       = 'https://github.com/prcyangli/IndepenDesk/releases/download/v0.4.1/IndepenDesk-Setup-0.4.1-x64.exe'
  checksum       = 'b8e52efb14a4286f0d2cdf151ee6311b400db60e98780fc1dd040185a8b3e57c'
  checksumType   = 'sha256'
  checksum64     = '788fa070a286caade941ad71b0629055f9d13d2aa7ccbfe98ef8160507e35e0b'
  checksumType64 = 'sha256'
  silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
  validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
