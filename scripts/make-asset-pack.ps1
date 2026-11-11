<#
  make-asset-pack.ps1 — build the distributable "asset pack" you host yourself.

  This bundles everything a *player* needs into one archive you upload once (to
  your Google Drive / MEGA / torrent / etc.). Then players either:
    - download it, unzip it over this folder, and run INSTALL.bat, OR
    - run INSTALL.bat -AssetPackUrl <your link> and it fetches + installs it.

  The pack contains (NOT committed to the repo — these are game files you own):
    gcg_offline.apk, assets/obb/*.obb, assets/Bundles/ (the ~4.5 GB the game
    needs), assets/device_assets/ (version.data + bundle list), the sync script,
    and the installer. Live2D models are reference-only and excluded by default.

  Output: dist/gcg_assets.zip  (or split parts with -SplitGB).
  Requirements: 7-Zip (preferred, for splitting) OR Windows tar.exe (built in).

  Usage:
    powershell -ExecutionPolicy Bypass -File make-asset-pack.ps1
    powershell -ExecutionPolicy Bypass -File make-asset-pack.ps1 -SplitGB 1.9   # for 2GB-limited hosts
    powershell -ExecutionPolicy Bypass -File make-asset-pack.ps1 -IncludeLive2D
#>
[CmdletBinding()]
param([double]$SplitGB = 0, [switch]$IncludeLive2D)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# what goes in the pack (relative to repo root)
$items = @(
    "gcg_offline.apk",
    "INSTALL.bat",
    "scripts/install.ps1",
    "assets/sync_device_assets.ps1",
    "assets/device_assets/version.data",
    "assets/device_assets/added_bundles.txt",
    "assets/obb",
    "assets/Bundles",
    "docs/PLAY-pack.txt"
)
if ($IncludeLive2D) { $items += "assets/live2d-models" }

foreach ($i in $items) {
    if (-not (Test-Path (Join-Path $root $i))) {
        if ($i -eq "docs/PLAY-pack.txt") { continue }  # generated below
        throw "Missing from pack input: $i  (build the APK / stage assets first)"
    }
}

# short readme placed inside the pack
$play = Join-Path $root "docs\PLAY-pack.txt"
@"
Girl Cafe Gun - Offline (EN v1.0.9) - asset pack
================================================
1. Unzip this so gcg_offline.apk and the assets\ folder sit in the same folder as INSTALL.bat.
2. Start your arm64 emulator (MuMu Player 12, Root ON) or connect an arm64 phone (USB debugging).
3. Double-click INSTALL.bat.  It installs everything and launches the game. No internet needed.
First launch: Terms screen -> "Agree to all & start". Title shows "Local" -> New Account -> play.
Full guide + credits + IP notice: see the project repo (docs\INSTALL.md, CREDITS.md).
Scan anything you download before running it.
"@ | Set-Content -Encoding UTF8 $play

$sizeGB = [math]::Round((($items | ForEach-Object { Get-ChildItem (Join-Path $root $_) -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum }).Sum | Measure-Object -Sum).Sum / 1GB, 2)
Write-Host "Packing ~$sizeGB GB ..." -ForegroundColor Cyan

$7z = (Get-Command 7z -ErrorAction SilentlyContinue)
if (-not $7z) { foreach ($c in @("C:\Program Files\7-Zip\7z.exe")) { if (Test-Path $c) { $7z = $c } } } else { $7z = $7z.Source }

Push-Location $root
try {
    if ($7z) {
        $out = Join-Path $dist "gcg_assets.7z"
        Remove-Item "$out*" -Force -ErrorAction SilentlyContinue
        $args = @("a", "-tzip", "-mx=1", $out) + $items
        if ($SplitGB -gt 0) { $args = @("a", "-tzip", "-mx=1", "-v$([int]($SplitGB*1024))m", $out) + $items }
        & $7z @args
        Write-Host "`nPack: $out*" -ForegroundColor Green
    } else {
        if ($SplitGB -gt 0) { Write-Warning "7-Zip not found; cannot split. Producing a single zip. Install 7-Zip to split for 2GB-limited hosts." }
        $out = Join-Path $dist "gcg_assets.zip"
        Remove-Item $out -Force -ErrorAction SilentlyContinue
        & tar.exe -a -c -f $out @items          # bsdtar writes a zip (zip64 for >4GB)
        Write-Host "`nPack: $out" -ForegroundColor Green
    }
} finally { Pop-Location }

Write-Host @"

Next steps (to give people a download link):
  1. Upload the file(s) in dist\ to your host (Google Drive / MEGA / etc.).
  2. Share that link. People run:  INSTALL.bat -AssetPackUrl <your link>
     (single-zip links work directly; for split parts, have people download all
      parts and run INSTALL.bat locally after joining them.)
"@ -ForegroundColor Yellow
