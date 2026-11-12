<#
.SYNOPSIS
  One command: server source -> signed offline APK -> shareable archives in dist\.

.DESCRIPTION
  1. dotnet publish the server (linux-bionic-arm64, PublishAot=false)
  2. rebuild libsssjdata.so + runtime .so, shim, host, installer; repack the base APK; zipalign; sign (apk-build\build_offline_apk.py --publish)
  3. verify: apksigner, embedded server DLL hash == fresh publish hash
  4. build GirlCafeGun-data.gcg (or reuse it when no input is newer)
  5. stage + zip two packs, with INSTALL.txt, VERSION.txt, CHECKSUMS.sha256:
       GirlCafeGun-<ver>-update.zip  APK only        (players who already ran the data install)
       GirlCafeGun-<ver>-full.zip    APK + data pack (first install)
  6. check the pack contents against an allowlist (no source, keystore, tools, logs)

  The work runs in its own visible PowerShell window, detached from the caller (the Claude app may restart).
  The caller only waits for the done marker. Log: dist\logs\package-<stamp>.log

.PARAMETER Foreground    run inline, no detach
.PARAMETER RebuildData   rebuild the data package even if it looks current
.PARAMETER SplitSize     7-Zip volume size for the full pack, e.g. 3900m (FAT32 / upload limits). Default: one file.
.PARAMETER BaseApk       base APK (stock game + shim + fixes). Default: gcg_offline_fixed.apk in the project root.
#>
param(
    [switch]$Foreground,
    [switch]$RebuildData,
    [string]$SplitSize = "",
    [string]$BaseApk = "",
    [switch]$Inner,
    [string]$Stamp = ""
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Dist = Join-Path $Root "dist"
$Pkg = "com.biligameglobal.girlcafegungp"
$SevenZip = "C:\Program Files\7-Zip\7z.exe"
$BT = "C:\android-build\bt\android-15"
$jdk = (Get-ChildItem "C:\android-build\jdk" -Directory | Select-Object -First 1).FullName
$env:JAVA_HOME = $jdk
$env:PATH = "$jdk\bin;$env:PATH"

function Step($m) { Write-Host ("`n=== " + $m + "  [" + (Get-Date -Format HH:mm:ss) + "]") -ForegroundColor Cyan }
function Run([string]$exe, [string[]]$a) {
    & $exe @a
    if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE" }
}
function Sha256([string]$p) { (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash.ToLower() }

# ---------------------------------------------------------------- outer: detach and wait
if (-not $Inner -and -not $Foreground) {
    New-Item -ItemType Directory -Force (Join-Path $Dist "logs") | Out-Null
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $log = Join-Path $Dist "logs\package-$stamp.log"
    $done = Join-Path $Dist "logs\package-$stamp.done"
    $cmd = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "{0}" -Inner -Stamp {1}' -f $PSCommandPath, $stamp
    if ($RebuildData) { $cmd += " -RebuildData" }
    if ($SplitSize) { $cmd += " -SplitSize $SplitSize" }
    if ($BaseApk) { $cmd += ' -BaseApk "{0}"' -f $BaseApk }
    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd; CurrentDirectory = $Root }
    if ($r.ReturnValue -ne 0) { throw "could not start the worker (Win32_Process.Create = $($r.ReturnValue))" }
    Write-Host "worker pid $($r.ProcessId) started in its own window; log: $log"
    while (-not (Test-Path $done)) { Start-Sleep -Seconds 5 }
    Start-Sleep -Milliseconds 500
    $code = [int](Get-Content $done -First 1)
    $res = Join-Path $Dist "last-result.txt"
    if ($code -eq 0 -and (Test-Path $res)) { Get-Content $res } else { Get-Content $log -Tail 40 }
    exit $code
}

# ---------------------------------------------------------------- inner: the actual build
if ($Inner) {
    if (-not $Stamp) { $Stamp = Get-Date -Format "yyyyMMdd-HHmmss" }
    New-Item -ItemType Directory -Force (Join-Path $Dist "logs") | Out-Null
    $log = Join-Path $Dist "logs\package-$Stamp.log"
    $done = Join-Path $Dist "logs\package-$Stamp.done"
    Start-Transcript -Path $log -Force | Out-Null
}
$exit = 0
try {
    Set-Location $Root
    $t0 = Get-Date

    Step "version stamp"
    $hash = (git rev-parse --short HEAD).Trim()
    # tracked files that feed the build; untracked scratch files do not count
    $dirty = (git status --porcelain --untracked-files=no -- server apk-build tools docs assets/device_assets | Out-String).Trim()
    $date = Get-Date -Format "yyyy-MM-dd"
    $ver = "$($date.Replace('-',''))-$hash" + $(if ($dirty) { "-dirty" } else { "" })
    if ($dirty) { Write-Warning "tracked build inputs are uncommitted; the version carries '-dirty':`n$dirty" }
    Write-Host "version $ver"

    $work = Join-Path $Dist "work"
    $pub = Join-Path $work "publish"
    $stage = Join-Path $work "stage"
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
    New-Item -ItemType Directory -Force $pub, $stage | Out-Null

    Step "dotnet publish (linux-bionic-arm64, JIT runtime)"
    Run dotnet @("publish", (Join-Path $Root "server\src\SssjServer.App\SssjServer.App.csproj"), "-c", "Release",
        "-r", "linux-bionic-arm64", "--self-contained", "true", "-p:PublishAot=false", "-o", $pub, "--nologo", "-v", "minimal")

    Step "build, repack, align, sign APK"
    if (-not $BaseApk) { $BaseApk = Join-Path $Root "gcg_offline_fixed.apk" }
    if (-not (Test-Path $BaseApk)) { throw "base APK missing: $BaseApk" }
    $apk = Join-Path $work "GirlCafeGun.apk"
    Run python @((Join-Path $Root "apk-build\build_offline_apk.py"), $BaseApk, $apk, "--publish", $pub,
        "--setting", (Join-Path $Root "server\setting"))

    Step "verify APK"
    Run (Join-Path $BT "apksigner.bat") @("verify", "--verbose", $apk)
    $chk = @'
import sys, zipfile, tarfile, io, hashlib, os
apk, pub = sys.argv[1], sys.argv[2]
z = zipfile.ZipFile(apk)
t = tarfile.open(fileobj=io.BytesIO(z.read("lib/arm64-v8a/libsssjdata.so")), mode="r:gz")
bad = 0
for n in ("SssjServer.App.dll", "SssjServer.Game.dll", "SssjServer.Core.dll", "SssjServer.Protocol.dll"):
    a = hashlib.sha256(t.extractfile("./" + n).read()).hexdigest()
    b = hashlib.sha256(open(os.path.join(pub, n), "rb").read()).hexdigest()
    print(("OK  " if a == b else "FAIL"), n, a)
    bad += a != b
for so in ("libcoreclr", "libhostfxr", "libhostpolicy"):
    a = hashlib.sha256(z.read("lib/arm64-v8a/%s.so" % so)).hexdigest()
    b = hashlib.sha256(open(os.path.join(pub, so + ".so"), "rb").read()).hexdigest()
    print(("OK  " if a == b else "FAIL"), so + ".so (lib entry)", a)
    bad += a != b
sets = [m for m in t.getnames() if m.startswith("./setting/")]
print("setting files in embedded tar:", len(sets))
bad += len(sets) < 100
sys.exit(1 if bad else 0)
'@
    $chkPy = Join-Path $work "verify_apk.py"
    Set-Content -Path $chkPy -Value $chk -Encoding utf8
    Run python @("-I", $chkPy, $apk, $pub)

    Step "data package"
    $gcg = Join-Path $Root "GirlCafeGun-data.gcg"
    $needData = $RebuildData -or -not (Test-Path $gcg)
    if (-not $needData) {
        $gt = (Get-Item $gcg).LastWriteTimeUtc
        $inputs = @(Get-Item (Join-Path $Root "apk-build\make_data_package.py")) +
            @(Get-ChildItem (Join-Path $Root "assets\device_assets") -Recurse -File) +
            @(Get-ChildItem (Join-Path $Root "assets\obb") -File)
        $newer = $inputs | Where-Object { $_.LastWriteTimeUtc -gt $gt } | Select-Object -First 1
        if ($newer) { Write-Host "input newer than data package: $($newer.FullName)"; $needData = $true }
    }
    if ($needData) { Run python @((Join-Path $Root "apk-build\make_data_package.py"), $gcg) }
    else { Write-Host "reusing $gcg (no input is newer; -RebuildData forces a rebuild)" }

    Step "stage files"
    $apkName = "GirlCafeGun.apk"
    Copy-Item $apk (Join-Path $stage $apkName)
    $dataName = "GirlCafeGun-data.gcg"
    $apkSha = Sha256 (Join-Path $stage $apkName)
    $dataSha = Sha256 $gcg
    $apkSize = (Get-Item (Join-Path $stage $apkName)).Length
    $dataSize = (Get-Item $gcg).Length
    $dataGB = [math]::Round($dataSize / 1GB, 1)
    $apkMB = [math]::Round($apkSize / 1MB)
    $needGB = [math]::Ceiling($dataSize * 2 / 1GB) + 1
    "$apkSha *$apkName`n$dataSha *$dataName`n" | Set-Content -Path (Join-Path $stage "CHECKSUMS.sha256") -Encoding ascii -NoNewline
    "Girl Cafe Gun offline (EN v1.0.9)`nbuild $ver`ncommit $hash`nbuilt $(Get-Date -Format 'yyyy-MM-dd HH:mm')`n" |
        Set-Content -Path (Join-Path $stage "VERSION.txt") -Encoding ascii -NoNewline

    function Write-Install([string]$path, [bool]$full) {
        $files = if ($full) { "  $apkName           the game (about $apkMB MB)`r`n  $dataName     the game data (about $dataGB GB)" } else { "  $apkName           the game (about $apkMB MB)" }
        $first = if ($full) { @"
FIRST INSTALL
1. Copy both files to the phone (for example into Downloads). Check them against CHECKSUMS.sha256 if you like.
2. Open $apkName and install it. If Android asks, allow installing apps from this source.
3. Open the game. A window "Install game data" appears.
4. Tap "Choose file" and select $dataName.
5. Wait for the progress bar (a few seconds to 15 minutes, depending on the phone).
6. Tap "Delete file" to free $dataGB GB (or "Keep file"). The game closes.
7. Open the game again. Tap "New Account" on the title screen to start.

The phone needs about $needGB GB free during the install ($dataGB GB for the data file + $dataGB GB for the installed data).
"@ } else { @"
UPDATE (this pack has no game data)
You need the data file only for the first install. If the game already runs on the phone, just install this APK over it.
No uninstall is needed. Saves and game data stay. The new APK has the same signature as the old one.
If you have never installed the game, get the FULL pack instead.
"@ }
        $text = @"
Girl Cafe Gun (offline, English) - install guide        build $ver
=====================================================================

Files:
$files

$first

UPDATE WITH A PC (adb)
  adb install -r $apkName
  (USB debugging on, phone connected. Saves and game data stay.)

NOTES
- The phone must be a 64-bit (arm64) Android phone. 32-bit phones cannot run it.
- Works offline, also in airplane mode. Nothing is sent anywhere. No root, no PC and no account needed.
- If an official copy of the game is installed, uninstall it first. The signatures differ.
- Black bar on one side of the screen: Settings > Display > Camera cutout > Girl Cafe Gun > "Show camera cutout", then restart the game.
- Your progress is stored on the phone. Uninstalling the game deletes it.
- If the data install stops with an error, the data file is usually incomplete. Copy it again and repeat from step 3.
- Fan-made private build, not affiliated with the game's publisher.
"@
        ($text -replace "`r?`n", "`r`n") | Set-Content -Path $path -Encoding ascii
    }

    $upDir = Join-Path $work "update"; $fullDir = Join-Path $work "full"
    New-Item -ItemType Directory -Force $upDir, $fullDir | Out-Null
    foreach ($f in "VERSION.txt") { Copy-Item (Join-Path $stage $f) $upDir; Copy-Item (Join-Path $stage $f) $fullDir }
    Copy-Item (Join-Path $stage $apkName) $upDir; Copy-Item (Join-Path $stage $apkName) $fullDir
    "$apkSha *$apkName`n" | Set-Content (Join-Path $upDir "CHECKSUMS.sha256") -Encoding ascii -NoNewline
    Copy-Item (Join-Path $stage "CHECKSUMS.sha256") $fullDir
    Write-Install (Join-Path $upDir "INSTALL.txt") $false
    Write-Install (Join-Path $fullDir "INSTALL.txt") $true
    # hard link: no 5 GB copy (same volume)
    New-Item -ItemType HardLink -Path (Join-Path $fullDir $dataName) -Target $gcg | Out-Null

    Step "zip packs"
    # drop older packs of other versions; keep logs
    Get-ChildItem $Dist -File -Filter "GirlCafeGun-*" | Remove-Item -Force
    $upZip = Join-Path $Dist "GirlCafeGun-$ver-update.zip"
    $fullZip = Join-Path $Dist "GirlCafeGun-$ver-full.zip"
    Push-Location $upDir;   Run $SevenZip @("a", "-tzip", "-mx0", "-bso0", "-bsp0", $upZip, "*"); Pop-Location
    $fa = @("a", "-tzip", "-mx0", "-bso0", "-bsp0")
    if ($SplitSize) { $fa += "-v$SplitSize" }
    Push-Location $fullDir; Run $SevenZip ($fa + @($fullZip, "*")); Pop-Location

    Step "check pack contents"
    $allow = @("CHECKSUMS.sha256", "GirlCafeGun-data.gcg", "GirlCafeGun.apk", "INSTALL.txt", "VERSION.txt")
    $targets = @(@{ z = $upZip; max = 4 }, @{ z = $(if ($SplitSize) { "$fullZip.001" } else { $fullZip }); max = 5 })
    foreach ($t in $targets) {
        $names = (& $SevenZip l -slt $t.z | Select-String '^Path = ' | ForEach-Object { $_.Line.Substring(7) }) |
            Where-Object { $_ -ne (Split-Path $t.z -Leaf) }
        Write-Host ("{0}: {1}" -f (Split-Path $t.z -Leaf), ($names -join ", "))
        $extra = $names | Where-Object { $allow -notcontains $_ }
        if ($extra) { throw "unexpected file in pack: $($extra -join ', ')" }
        if (@($names).Count -gt $t.max) { throw "too many entries in $($t.z)" }
        $bad = $names | Where-Object { $_ -match '\.(keystore|jks|cs|csproj|sln|pdb|py|c|java|log|json|db)$|\.git|\.mcp|/|\\' }
        if ($bad) { throw "forbidden entry: $($bad -join ', ')" }
    }
    Run $SevenZip @("t", "-bso0", "-bsp0", $(if ($SplitSize) { "$fullZip.001" } else { $fullZip }))
    Run $SevenZip @("t", "-bso0", "-bsp0", $upZip)
    Write-Host "archive test OK"

    Step "checksums + result"
    $outs = @(Get-ChildItem $Dist -File -Filter "GirlCafeGun-$ver-*")
    $sums = $outs | ForEach-Object { "{0} *{1}" -f (Sha256 $_.FullName), $_.Name }
    $sums -join "`n" | Set-Content (Join-Path $Dist "SHA256SUMS.txt") -Encoding ascii
    Remove-Item -Recurse -Force $fullDir, $upDir, $stage   # hard link removed; the .gcg in the project root stays
    $lines = @("PACKAGE OK  build $ver  ($([int]((Get-Date) - $t0).TotalSeconds) s)")
    foreach ($o in $outs) { $lines += ("  {0}  {1:N1} MB" -f $o.FullName, ($o.Length / 1MB)) }
    $lines += "  APK  $apkMB MB  sha256 $apkSha"
    $lines += "  data pack  $dataGB GB  sha256 $dataSha"
    $lines += "  commit $hash$(if ($dirty) { ' (DIRTY inputs)' })"
    $lines += "  checksums file: $(Join-Path $Dist 'SHA256SUMS.txt')"
    $lines += "  signed APK also kept at: $(Join-Path $work $apkName)"
    $lines | Set-Content (Join-Path $Dist "last-result.txt") -Encoding utf8
    $lines | ForEach-Object { Write-Host $_ -ForegroundColor Green }
}
catch {
    $exit = 1
    Write-Host ("PACKAGE FAILED: " + $_.Exception.Message) -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
}
finally {
    if ($Inner) {
        Stop-Transcript | Out-Null
        Set-Content -Path $done -Value $exit -Encoding ascii
    }
}
exit $exit
