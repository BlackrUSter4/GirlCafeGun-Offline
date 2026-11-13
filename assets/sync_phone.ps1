# Brings a NON-ROOT Android phone's game files in line with this package (USB debugging on, one device attached).
# Pushes only what is missing or different, and fails loudly. Safe to re-run.
#   powershell -File assets\sync_phone.ps1            (uses the only attached device)
#   powershell -File assets\sync_phone.ps1 -Device R5CX10F083V
#
# What it installs (all into the game's public folders, no root needed):
#  1. the OBB                    -> /sdcard/Android/obb/<pkg>/
#  2. merged version.data        -> files/version.data   (the game reverts it to the OBB table after a broken boot)
#  3. every bundle in Bundles\ that the OBB lacks, plus Raw\ (videos) and AutoAudio\ (voices, music)
#  4. device_assets\overrides\*  -> files/   (lua.u and versioncrc.json always go together: the game CRC-checks lua.u)
#  5. the uncensor switch        -> files/l2d/girl111/love.u
param([string]$Device = "")
$ErrorActionPreference = "Stop"
$adb = "C:\platform-tools\adb.exe"
$pkg = "com.biligameglobal.girlcafegungp"
$files = "/sdcard/Android/data/$pkg/files"
$obbDir = "/sdcard/Android/obb/$pkg"
$here = $PSScriptRoot
$bundles = Join-Path $here "Bundles"
$overrides = Join-Path $here "device_assets\overrides"
$obbLocal = Get-ChildItem (Join-Path $here "obb") -Filter "main.*.obb" | Select-Object -First 1
$sel = @(); if ($Device) { $sel = @("-s", $Device) }

function Adb([string[]]$a) {
    # adb prints progress on stderr; judge success by the exit code only
    $ErrorActionPreference = "Continue"
    $out = & $adb @sel @a 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw "adb $($a -join ' ') failed: $out" }
    return $out
}
function Sh([string]$cmd) { return Adb @("shell", $cmd) }

$state = (& $adb @sel get-state 2>&1)
if ("$state".Trim() -ne "device") { throw "No authorised device (adb get-state: $state). Turn on USB debugging and accept the prompt." }

# 1. OBB
$remoteObb = "$obbDir/$($obbLocal.Name)"
$remoteSize = (Sh "stat -c %s '$remoteObb' 2>/dev/null || echo 0") | Select-Object -Last 1
if ([int64]"$remoteSize".Trim() -ne $obbLocal.Length) {
    Write-Host "pushing OBB ($([math]::Round($obbLocal.Length/1GB,2)) GB)"
    Sh "mkdir -p '$obbDir'" | Out-Null
    Adb @("push", $obbLocal.FullName, $remoteObb) | Out-Null
} else { Write-Host "OBB ok" }

# list what is on the phone (relative path -> size)
Sh "mkdir -p '$files'" | Out-Null
$onPhone = @{}
foreach ($l in (Sh "cd '$files' && find . -type f -exec stat -c '%s|%n' {} +")) {
    $p = "$l".Split("|", 2); if ($p.Count -eq 2) { $onPhone[$p[1].Substring(2)] = [int64]$p[0] }
}

# OBB entries, so bundles the OBB already has are never overridden by Bundles\ copies
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::OpenRead($obbLocal.FullName)
$inObb = @{}; foreach ($e in $z.Entries) { if ($e.FullName.StartsWith("assets/")) { $inObb[$e.FullName.Substring(7)] = 1 } }
$z.Dispose()

# 2-5: build the list of (source file, phone path)
$push = New-Object System.Collections.Generic.List[object]
function Want([string]$src, [string]$rel) {
    $len = (Get-Item -LiteralPath $src).Length
    if (-not $onPhone.ContainsKey($rel) -or $onPhone[$rel] -ne $len) { $push.Add(@($src, $rel)) }
}
Want (Join-Path $here "device_assets\version.data") "version.data"
foreach ($f in Get-ChildItem $bundles -Recurse -File) {
    $rel = $f.FullName.Substring($bundles.Length + 1).Replace('\', '/')
    if ($rel -like "*.orig" -or $rel -eq "remoteversion.data") { continue }
    $isAsset = $rel -like "*.u" -or $rel -like "Raw/*" -or $rel -like "AutoAudio/*"
    if ($isAsset -and -not $inObb.ContainsKey($rel)) { Want $f.FullName $rel }
}
foreach ($f in Get-ChildItem $overrides -Recurse -File) {
    if ($f.Name -eq "README.txt") { continue }
    Want $f.FullName ($f.FullName.Substring($overrides.Length + 1).Replace('\', '/'))
}
# lua.u and its CRC file travel together
$names = $push | ForEach-Object { $_[1] }
if (($names -contains "lua.u") -xor ($names -contains "versioncrc.json")) {
    foreach ($n in "lua.u", "versioncrc.json") { if ($names -notcontains $n) { $push.Add(@((Join-Path $overrides $n), $n)) } }
}

if ($push.Count -gt 0) {
    Write-Host "pushing $($push.Count) files"
    $stage = Join-Path $env:TEMP "gcg_sync_stage"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    foreach ($it in $push) {
        $dst = Join-Path $stage ($it[1].Replace('/', '\'))
        New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
        Copy-Item -LiteralPath $it[0] $dst
    }
    $tar = Join-Path $env:TEMP "gcg_sync.tar"
    & "$env:SystemRoot\System32\tar.exe" -cf $tar -C $stage .
    if ($LASTEXITCODE -ne 0) { throw "tar failed" }
    Adb @("push", $tar, "/data/local/tmp/gcg_sync.tar") | Out-Null
    Sh "cd '$files' && tar -xmf /data/local/tmp/gcg_sync.tar && rm /data/local/tmp/gcg_sync.tar && echo OK" | Out-Null
    Remove-Item $tar, $stage -Recurse -Force
} else { Write-Host "game files up to date" }

# 5. uncensor switch (only its existence matters)
Sh "mkdir -p '$files/l2d/girl111' && [ -f '$files/l2d/girl111/love.u' ] || echo love > '$files/l2d/girl111/love.u'" | Out-Null
Sh "chmod -R a+rw '$files' 2>/dev/null; true" | Out-Null
Write-Host "done"
