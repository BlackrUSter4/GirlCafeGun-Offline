<#
  install.ps1 — one-shot offline install of Girl Cafe Gun (EN v1.0.9).

  What it does, start to finish:
    1. finds adb and your emulator/device,
    2. makes sure the asset pack (APK + OBB + bundles) is present locally — if it
       isn't and you set -AssetPackUrl (or $AssetPackUrl below), it downloads and
       extracts it for you,
    3. installs the offline APK (keeps an existing install via `install -r`),
    4. puts the OBB in place,
    5. syncs every content bundle the game needs (sync_device_assets.ps1),
    6. launches the game.

  Nothing leaves the device. The server is embedded in the APK and runs on
  127.0.0.1. Works in airplane mode.

  Usage:
    powershell -ExecutionPolicy Bypass -File install.ps1
    powershell -ExecutionPolicy Bypass -File install.ps1 -Device 127.0.0.1:16448
    powershell -ExecutionPolicy Bypass -File install.ps1 -AssetPackUrl https://your-host/gcg_assets.zip

  Requirements: an arm64 Android emulator (MuMu Player 12 recommended) with Root
  on, or an arm64 phone. adb on PATH or at C:\platform-tools\adb.exe.
#>
[CmdletBinding()]
param(
    [string]$Device = "",
    # Put your hosted asset-pack URL here (or pass -AssetPackUrl). The pack is the
    # ~10 GB of game files you host yourself (Drive / MEGA / torrent). Leave blank
    # to use assets already present next to this repo.
    [string]$AssetPackUrl = "",
    [switch]$SkipLaunch,
    # Skip the love.u uncensor patch (restores the Live2D touch/flirting interactions
    # the global build suppresses). On by default; pass -NoUncensor to leave it censored.
    [switch]$NoUncensor
)

$ErrorActionPreference = "Stop"
$pkg = "com.biligameglobal.girlcafegungp"
$activity = "com.crasheye.client.api.unity3d.MainActivity"
$root = Split-Path $PSScriptRoot -Parent          # repo root (GCG2-Offline)
$assets = Join-Path $root "assets"
$apk = Join-Path $root "gcg_offline.apk"

function Find-Adb {
    foreach ($c in @("adb", "C:\platform-tools\adb.exe", "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe")) {
        $p = (Get-Command $c -ErrorAction SilentlyContinue)
        if ($p) { return $p.Source }
        if (Test-Path $c) { return $c }
    }
    throw "adb not found. Install Android platform-tools and put adb on PATH (or at C:\platform-tools\adb.exe)."
}
$adb = Find-Adb
Write-Host "adb: $adb" -ForegroundColor DarkGray

function Pick-Device {
    if ($Device) { & $adb connect $Device *>$null; return $Device }
    $lines = (& $adb devices) -split "`n" | Where-Object { $_ -match "`tdevice$" }
    if ($lines.Count -eq 0) { throw "No device. Start your emulator (MuMu) and enable its ADB, or plug in your phone with USB debugging." }
    $d = ($lines[0] -split "`t")[0]
    if ($lines.Count -gt 1) { Write-Warning "Multiple devices; using $d. Pass -Device to choose." }
    return $d
}
$Device = Pick-Device
Write-Host "device: $Device" -ForegroundColor Cyan

# ── asset pack ────────────────────────────────────────────────────────────────
if (-not (Test-Path $apk) -or -not (Test-Path (Join-Path $assets "obb"))) {
    if (-not $AssetPackUrl) {
        throw "Game files not found and no -AssetPackUrl given.`n" +
              "Either put gcg_offline.apk + the assets/ folder next to this repo, or re-run with -AssetPackUrl <your hosted pack>."
    }
    Write-Host "Downloading asset pack from $AssetPackUrl ..." -ForegroundColor Yellow
    $tmp = Join-Path $env:TEMP "gcg_assets_pack"
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    $dl = Join-Path $tmp "pack.zip"
    Invoke-WebRequest -Uri $AssetPackUrl -OutFile $dl
    Write-Host "Extracting ..." -ForegroundColor Yellow
    Expand-Archive -Path $dl -DestinationPath $root -Force
    Remove-Item $dl -Force
}
if (-not (Test-Path $apk)) { throw "gcg_offline.apk still not found at $apk" }

# ── install APK ───────────────────────────────────────────────────────────────
$installed = (& $adb -s $Device shell pm path $pkg) 2>$null
if ($installed) {
    Write-Host "Updating existing install (install -r, keeps data) ..." -ForegroundColor Yellow
    & $adb -s $Device install -r "$apk"
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "install -r failed (likely a different signature). Uninstalling the old copy and reinstalling..."
        & $adb -s $Device uninstall $pkg | Out-Null
        & $adb -s $Device install "$apk"
    }
} else {
    Write-Host "Installing APK ..." -ForegroundColor Yellow
    & $adb -s $Device install "$apk"
}
if ($LASTEXITCODE -ne 0) { throw "APK install failed." }

# ── OBB ───────────────────────────────────────────────────────────────────────
$obb = Get-ChildItem (Join-Path $assets "obb") -Filter "*.obb" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $obb) { throw "No OBB found in assets/obb/." }
Write-Host "Installing OBB ($([math]::Round($obb.Length/1GB,2)) GB) ..." -ForegroundColor Yellow
& $adb -s $Device push "$($obb.FullName)" "/data/local/tmp/main.obb" | Out-Null
& $adb -s $Device shell "su 0 sh -c 'mkdir -p /sdcard/Android/obb/$pkg; cp /data/local/tmp/main.obb /sdcard/Android/obb/$pkg/main.10009.$pkg.obb; chmod 666 /sdcard/Android/obb/$pkg/main.*.obb; rm /data/local/tmp/main.obb'"

# ── content bundles ───────────────────────────────────────────────────────────
Write-Host "Syncing content bundles (this is the big step; 4.5 GB on first run) ..." -ForegroundColor Yellow
& powershell -ExecutionPolicy Bypass -File (Join-Path $assets "sync_device_assets.ps1") -Device $Device

# ── uncensor patch (love.u) ─────────────────────────────────────────────────────
# The EN build is censored by the OFFICIAL server sending a reduced Live2D enable
# level / harmonize (HX) flag; the client bypasses it when l2d/girl111/love.u exists
# (S2CCall.lua: SetLive2DEnableLevel). The file only needs to exist, so we make it a
# copy of the already-synced girl111/l2d01.u. Our offline server sends no censor flag,
# so with this in place the touch/flirting interactions run at full level.
if (-not $NoUncensor) {
    Write-Host "Applying uncensor patch (love.u) ..." -ForegroundColor Yellow
    $files = "/data/media/0/Android/data/$pkg/files"
    & $adb -s $Device shell "su 0 sh -c 'd=$files/l2d/girl111; if [ -f `$d/l2d01.u ]; then cp `$d/l2d01.u `$d/love.u; chown 2000:2000 `$d/love.u; chmod 666 `$d/love.u; echo love.u_created; else echo l2d01_missing; fi'"
}

# ── launch ────────────────────────────────────────────────────────────────────
if (-not $SkipLaunch) {
    Write-Host "Launching ..." -ForegroundColor Green
    & $adb -s $Device shell "am start -n $pkg/$activity" | Out-Null
}
Write-Host "`nDone. First launch shows a Terms screen -> 'Agree to all & start'." -ForegroundColor Green
Write-Host "At the title (server shows 'Local') tap New Account to play. No internet needed." -ForegroundColor Green
