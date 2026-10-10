# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.

# SteamFriendMonitor publish script
# Usage: powershell -ExecutionPolicy Bypass -File publish-all.ps1
# Output (self-contained compressed single files, zero dependency on target machines):
#   publish\SteamFriendMonitor.exe                   GUI (Windows x64)
#   publish\cli\<rid>\SteamFriendMonitor.Cli*        CLI (win-x64 / linux-x64 / linux-arm64 / osx-x64 / osx-arm64)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# Prefer user-level SDK (for machines without a system-wide SDK)
$sdk = Join-Path $env:USERPROFILE ".dotnet-sdk\dotnet.exe"
if (-not (Test-Path $sdk)) { $sdk = "dotnet" }

# Clean old CLI output before republishing
if (Test-Path "$root\publish\cli") {
    Remove-Item "$root\publish\cli" -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "==> Publishing GUI (win-x64, self-contained)..." -ForegroundColor Cyan
& $sdk publish "$root\SteamFriendMonitor.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o "$root\publish"
if ($LASTEXITCODE -ne 0) { throw "GUI publish failed" }

foreach ($rid in @("win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")) {
    Write-Host "==> Publishing CLI ($rid, self-contained)..." -ForegroundColor Cyan
    & $sdk publish "$root\SteamFriendMonitor.Cli\SteamFriendMonitor.Cli.csproj" -c Release -r $rid --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o "$root\publish\cli\$rid"
    if ($LASTEXITCODE -ne 0) { throw "CLI ($rid) publish failed" }
}

Write-Host ""
Write-Host "==> Packaging zips (GUI/CLI x OS x arch)..." -ForegroundColor Cyan

$zips = @()
$stage = Join-Path $root "publish\stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

# Remove stale zips
Get-ChildItem "$root\publish" -Filter "SteamFriendMonitor-*.zip" -ErrorAction SilentlyContinue | Remove-Item -Force

# GUI: windows-x64
$guiStage = Join-Path $stage "SteamFriendMonitor-GUI-windows-x64\SteamFriendMonitor"
New-Item -ItemType Directory -Path $guiStage -Force | Out-Null
Copy-Item "$root\publish\SteamFriendMonitor.exe" $guiStage
Compress-Archive -Path $guiStage -DestinationPath "$root\publish\SteamFriendMonitor-GUI-windows-x64.zip" -Force
$zips += "SteamFriendMonitor-GUI-windows-x64.zip"

# CLI: rid -> os-arch
$ridMap = @{
    "win-x64"    = "windows-x64"
    "linux-x64"  = "linux-x64"
    "linux-arm64" = "linux-arm64"
    "osx-x64"    = "macos-x64"
    "osx-arm64"  = "macos-arm64"
}

foreach ($rid in $ridMap.Keys) {
    $osArch = $ridMap[$rid]
    $bin = Get-ChildItem "$root\publish\cli\$rid" -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "SteamFriendMonitor*" -and $_.Extension -ne ".pdb" } | Select-Object -First 1

    if ($bin) {
        $dir = Join-Path $stage "SteamFriendMonitor-CLI-$osArch\SteamFriendMonitor"
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Copy-Item $bin.FullName $dir
        Compress-Archive -Path $dir -DestinationPath "$root\publish\SteamFriendMonitor-CLI-$osArch.zip" -Force
        $zips += "SteamFriendMonitor-CLI-$osArch.zip"
    }
}

Remove-Item $stage -Recurse -Force

Write-Host ""
Write-Host "All done:" -ForegroundColor Green
$guiFile = Get-Item "$root\publish\SteamFriendMonitor.exe" -ErrorAction SilentlyContinue
if ($guiFile) { Write-Host ("  publish\SteamFriendMonitor.exe  (GUI, " + [math]::Round($guiFile.Length / 1MB, 1) + " MB)") }
foreach ($name in $zips) {
    $zip = Get-Item (Join-Path "$root\publish" $name)
    Write-Host ("  publish\" + $name + "  (" + [math]::Round($zip.Length / 1MB, 1) + " MB)")
}
