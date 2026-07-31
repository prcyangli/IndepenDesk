$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = 'independesk'
  fileType       = 'exe'
  url            = 'https://github.com/harungecit/IndepenDesk/releases/download/v0.4.0/IndepenDesk-Setup-0.4.0-x86.exe'
  url64bit       = 'https://github.com/harungecit/IndepenDesk/releases/download/v0.4.0/IndepenDesk-Setup-0.4.0-x64.exe'
  checksum       = 'ed758d44751184d4f22e0323c1adaecfb5c31a5790b04963adc1f94e7af84fe7'
  checksumType   = 'sha256'
  checksum64     = '8df75b880956f460a6c6a4c7dc59266b1ac0e1686f02df6410af4d6d4fabe360'
  checksumType64 = 'sha256'
  silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
  validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
