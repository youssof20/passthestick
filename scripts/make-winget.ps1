<#
  Writes the three winget manifest files for one release, with the installer hash filled in.
  Used by the release workflow; also runnable by hand:
    ./scripts/make-winget.ps1 -Version 0.2.0 -Repository youssof20/passthestick -Installer artifacts/releases/PassTheStick-win-Setup.exe -OutDir artifacts/winget
  Submitting the result to microsoft/winget-pkgs is a manual step (see HANDOFF.md).
#>
param(
  [Parameter(Mandatory)] [string] $Version,
  [Parameter(Mandatory)] [string] $Repository,
  [Parameter(Mandatory)] [string] $Installer,
  [Parameter(Mandatory)] [string] $OutDir
)

$ErrorActionPreference = 'Stop'
$owner = $Repository.Split('/')[0]
$id = "$owner.PassTheStick"
$hash = (Get-FileHash -Algorithm SHA256 -Path $Installer).Hash.ToUpperInvariant()
$url = "https://github.com/$Repository/releases/download/v$Version/PassTheStick-win-Setup.exe"
$date = (Get-Date).ToString('yyyy-MM-dd')
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$versionYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
"@

$installerYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: exe
Scope: user
InstallModes:
  - interactive
  - silent
InstallerSwitches:
  Silent: --silent
  SilentWithProgress: --silent
Installers:
  - Architecture: x64
    InstallerUrl: $url
    InstallerSha256: $hash
ReleaseDate: $date
ManifestType: installer
ManifestVersion: 1.6.0
"@

$localeYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: PassTheStick contributors
PackageName: PassTheStick
PackageUrl: https://github.com/$Repository
License: MIT
LicenseUrl: https://github.com/$Repository/blob/main/LICENSE
ShortDescription: Let friends play your single-player game over the internet by sending their keyboard, controller or phone input.
Tags:
  - gaming
  - remote-play
  - controller
  - emulator
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@

Set-Content -Path (Join-Path $OutDir "$id.yaml") -Value $versionYaml -Encoding utf8
Set-Content -Path (Join-Path $OutDir "$id.installer.yaml") -Value $installerYaml -Encoding utf8
Set-Content -Path (Join-Path $OutDir "$id.locale.en-US.yaml") -Value $localeYaml -Encoding utf8
Write-Host "Wrote winget manifests for $id $Version (installer sha256 $hash)"
