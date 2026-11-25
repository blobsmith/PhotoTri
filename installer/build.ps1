# Construit l'installeur de PhotoTri : publish\PhotoTri-Setup-<version>.exe
# Usage (depuis le dossier du projet) :  powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version 1.0.1
# Prérequis : SDK .NET 8 et Inno Setup 6 (winget install JRSoftware.InnoSetup).
param([string]$Version = "1.0.0")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# 1. Version autonome : .NET, modèles de visages et base des villes inclus, rien d'autre à installer.
$out = Join-Path $root "publish\PhotoTri"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish (Join-Path $root "src\PhotoTri.App") -c Release -r win-x64 --self-contained true -o $out -nologo -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "La compilation a échoué." }

# 2. Installeur.
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 est introuvable : winget install JRSoftware.InnoSetup" }
& $iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot "PhotoTri.iss")
if ($LASTEXITCODE -ne 0) { throw "La création de l'installeur a échoué." }

Get-Item (Join-Path $root "publish\PhotoTri-Setup-$Version.exe") | Select-Object FullName, @{ n = "Mo"; e = { [math]::Round($_.Length / 1MB) } }
