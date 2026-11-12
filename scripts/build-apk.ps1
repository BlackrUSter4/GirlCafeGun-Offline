<#
  build-apk.ps1 — bake the offline gcg_offline.apk from source.

  Pipeline (same one used to produce the shipped APK):
    0. publish the server (build-server.ps1) unless -SkipServer
    1. data tar  = whole publish (apphost + .so + .dll + .a mono comps) + setting
    2. the 7 runtime .so ALSO as lib-dir entries (exec-allowed; no root needed)
    3. patch the client's reachability gate in libil2cpp.so (airplane mode)
    4. compile the shim (libmain.so) + host (libsssjhost.so)   [NDK android30 clang]
    5. extract the stock libmain as libmain_orig.so from YOUR orig.apk
    6. repack (repack.py) -> zipalign -> apksigner  (key: apk-build/sssj.keystore)

  You must provide a pristine stock APK as -OrigApk (the official
  com.biligameglobal.girlcafegungp base.apk). Nothing from the game is shipped in
  this repo; this step reads it out of YOUR copy.

  Requirements: .NET 10 SDK, Android NDK r27, Android build-tools (35.x), Python 3.
  Usage:
    powershell -ExecutionPolicy Bypass -File build-apk.ps1 -OrigApk C:\path\to\base.apk
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OrigApk,
    [string]$Ndk     = "$env:LOCALAPPDATA\Android\Sdk\ndk\27.2.12479018",
    [string]$BuildTools = "$env:LOCALAPPDATA\Android\Sdk\build-tools\35.0.0",
    # android.jar for the first-run installer (javac + d8). Needs a JDK too (JAVA_HOME).
    [string]$AndroidJar = "$env:LOCALAPPDATA\Android\Sdk\platforms\android-35\android.jar",
    [switch]$SkipServer,
    # Skip step 7 (GirlCafeGun.apk: first-run data installer + gamepad axes). gcg_offline.apk is still built.
    [switch]$NoInstaller
)
$ErrorActionPreference = "Stop"
$root  = Split-Path $PSScriptRoot -Parent
$ab    = Join-Path $root "apk-build"
$work  = Join-Path $root "dist\apkbuild"
$lib   = Join-Path $work "lib\arm64-v8a"
$pub   = Join-Path $root "server\src\SssjServer.App\bin\Release\net10.0\linux-bionic-arm64\publish"
$setting = Join-Path $root "server\setting"

foreach ($p in @($OrigApk, (Join-Path $ab "shim.c"), (Join-Path $ab "host.c"), (Join-Path $ab "repack.py"), (Join-Path $ab "patch_il2cpp.py"), (Join-Path $ab "sssj.keystore"))) {
    if (-not (Test-Path $p)) { throw "Missing required input: $p" }
}
$cc = Join-Path $Ndk "toolchains\llvm\prebuilt\windows-x86_64\bin\aarch64-linux-android30-clang.cmd"
if (-not (Test-Path $cc)) { throw "NDK clang not found: $cc  (set -Ndk)" }
$zipalign = Join-Path $BuildTools "zipalign.exe"
$apksigner = Join-Path $BuildTools "apksigner.bat"
if (-not (Test-Path $zipalign)) { throw "zipalign not found: $zipalign  (set -BuildTools)" }

if (-not $SkipServer) { & (Join-Path $PSScriptRoot "build-server.ps1") }
if (-not (Test-Path $pub)) { throw "Server publish not found at $pub (run without -SkipServer)" }
if (-not (Test-Path $setting)) { throw "server/setting not found (needed in the data tar). It ships in the asset pack / extract from your game." }

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $lib | Out-Null
Copy-Item $OrigApk (Join-Path $work "orig.apk")
foreach ($f in @("shim.c","host.c","repack.py","patch_il2cpp.py","sssj.keystore")) { Copy-Item (Join-Path $ab $f) $work }

Push-Location $work
try {
    # 1. data tar
    $staging = Join-Path $work "staging"
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    Copy-Item (Join-Path $pub "SssjServer.App") $staging
    Copy-Item (Join-Path $pub "*.so") $staging;  Copy-Item (Join-Path $pub "*.dll") $staging
    Copy-Item (Join-Path $pub "*.json") $staging; Copy-Item (Join-Path $pub "*.a") $staging
    Copy-Item $setting (Join-Path $staging "setting") -Recurse
    # Build the .tar.gz with Python (Windows' bundled tar.exe/bsdtar can silently
    # truncate the gzip stream on large archives; Python's tarfile is reliable).
    $dataTar = ((Join-Path $lib "libsssjdata.so") -replace '\\','/')
    $stg = ($staging -replace '\\','/')
    python -c "import tarfile; tf=tarfile.open(r'$dataTar','w:gz'); tf.add(r'$stg', arcname='.'); tf.close()"
    if ($LASTEXITCODE -ne 0) { throw "data tar build failed" }
    # 2. runtime .so as lib-dir entries
    foreach ($so in @("libhostfxr","libhostpolicy","libcoreclr","libSystem.Native","libSystem.Globalization.Native","libSystem.IO.Compression.Native","libSystem.Security.Cryptography.Native.OpenSsl")) {
        Copy-Item (Join-Path $pub "$so.so") $lib
    }
    # 3. airplane-mode il2cpp patch (extract from orig.apk, patch, ship as lib entry)
    python -c "import zipfile; zipfile.ZipFile('orig.apk').extract('lib/arm64-v8a/libil2cpp.so','.')"
    python patch_il2cpp.py (Join-Path $lib "libil2cpp.so")
    # 4. shim + host
    & $cc -shared -fPIC -O2 -o (Join-Path $lib "libmain.so") shim.c -llog -landroid
    if ($LASTEXITCODE -ne 0) { throw "shim compile failed" }
    & $cc -fPIE -pie -O2 -o (Join-Path $lib "libsssjhost.so") host.c -llog -ldl
    if ($LASTEXITCODE -ne 0) { throw "host compile failed" }
    # 5. stock libmain -> libmain_orig.so (from YOUR orig.apk, not shipped in repo)
    python -c "import zipfile,shutil; z=zipfile.ZipFile('orig.apk'); f=z.open('lib/arm64-v8a/libmain.so'); open(r'$($lib -replace '\\','/')/libmain_orig.so','wb').write(f.read())"
    # 6. repack, align, sign
    python repack.py
    & $zipalign -f -p 4 gcg_offline_unsigned.apk gcg_offline_aligned.apk
    & $apksigner sign --ks sssj.keystore --ks-pass pass:sssjsssj --ks-key-alias sssj --key-pass pass:sssjsssj --out (Join-Path $root "gcg_offline.apk") gcg_offline_aligned.apk
    if ($LASTEXITCODE -ne 0) { throw "apksigner failed" }
} finally { Pop-Location }

$apk = Join-Path $root "gcg_offline.apk"
Write-Host "`nBuilt: $apk  ($([math]::Round((Get-Item $apk).Length/1MB,1)) MB)" -ForegroundColor Green
& $apksigner verify --print-certs $apk | Select-String "SHA-256" | Select-Object -First 1

# 7. phone package: the same APK plus the first-run data installer (classes2.dex) and raw joystick
#    axes for the gamepad layer. Players install GirlCafeGun.apk and pick the data file in the game
#    (HOW-TO-INSTALL.txt); no PC, adb or root. apk-build/make_data_package.py builds the data file.
if (-not $NoInstaller) {
    if (-not (Test-Path $AndroidJar)) { throw "android.jar not found: $AndroidJar  (set -AndroidJar, or pass -NoInstaller)" }
    if (-not $env:JAVA_HOME -or -not (Test-Path (Join-Path $env:JAVA_HOME "bin\javac.exe"))) { throw "JAVA_HOME must point to a JDK (javac) for the installer, or pass -NoInstaller" }
    $env:GCG_NDK = $Ndk; $env:GCG_BUILD_TOOLS = $BuildTools; $env:GCG_ANDROID_JAR = $AndroidJar
    $phoneApk = Join-Path $root "GirlCafeGun.apk"
    python -I (Join-Path $ab "build_offline_apk.py") $apk $phoneApk
    if ($LASTEXITCODE -ne 0) { throw "build_offline_apk.py failed" }
    Write-Host "Built: $phoneApk  ($([math]::Round((Get-Item $phoneApk).Length/1MB,1)) MB)" -ForegroundColor Green
}
