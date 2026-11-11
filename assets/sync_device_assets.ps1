# Brings a device's game files in line with what the server's events need, so a fresh install works without any manual
# step. launch_gcg.ps1 runs this before starting the game, and it does nothing when the device is already correct.
#
# Two things have to be right:
#  - files/version.data must be the merged table (device_assets/version.data, from tools/merge_version_table.py). The
#    OBB ships build 5, and without the merge any newer bundle reports "Missing prefab".
#  - every bundle the merge added must be on disk (device_assets/added_bundles.txt). Only those: a bundle the build 5
#    table already knows comes from the OBB, and a build 179 copy on disk could override it with a layout that doesn't
#    match the client's scripts.
#
# adb push into Android/data fails on this Android (the file is created, then removed), so files go through
# /data/local/tmp and su. Ownership has to end up as shell, or the game cannot read them.
param([string]$Device = "127.0.0.1:16448")
$adb = "C:\platform-tools\adb.exe"
$pkg = "com.biligameglobal.girlcafegungp"
$files = "/data/media/0/Android/data/$pkg/files"
$tmp = "/data/local/tmp"
$bundles = Join-Path $PSScriptRoot "Bundles"   # share layout: assets/Bundles sits next to this script
$table = Join-Path $PSScriptRoot "device_assets\version.data"
$added = Join-Path $PSScriptRoot "device_assets\added_bundles.txt"

# A script file on the device avoids three layers of quoting between PowerShell, adb and sh.
function RunOnDevice([string]$script) {
    $local = Join-Path $env:TEMP "gcg_sync.sh"
    [IO.File]::WriteAllText($local, $script.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
    & $adb -s $Device push $local "$tmp/gcg_sync.sh" 2>&1 | Out-Null
    Remove-Item $local
    return & $adb -s $Device shell su 0 sh "$tmp/gcg_sync.sh"
}

function ListMissing {
    $present = @{}
    foreach ($line in (RunOnDevice "mkdir -p $files; cd $files && find . -type f")) { $present[$line.Trim().TrimStart('.', '/')] = $true }
    return @(Get-Content $added | Where-Object { $_ -and -not $present.ContainsKey($_) -and (Test-Path -LiteralPath (Join-Path $bundles $_)) })
}

$wanted = (Get-FileHash $table -Algorithm MD5).Hash.ToLower()
$current = ((RunOnDevice "md5sum $files/version.data 2>/dev/null") -split '\s+')[0]
if ($current -ne $wanted) {
    Write-Host "version.data differs, installing merged table"
    & $adb -s $Device push $table "$tmp/gcg_version.data" 2>&1 | Out-Null
    # The first replacement keeps the shipped table, so the merge can always be undone.
    RunOnDevice @"
mkdir -p $files
if [ -f $files/version.data ] && [ ! -f $files/version.data.build5.bak ]; then cp $files/version.data $files/version.data.build5.bak; fi
cp $tmp/gcg_version.data $files/version.data
chown 2000:2000 $files/version.data
chmod 666 $files/version.data
rm $tmp/gcg_version.data
"@ | Out-Null
}

$missing = ListMissing
if ($missing.Count -eq 0) {
    Write-Host "device assets up to date"
    return
}

Write-Host "installing $($missing.Count) missing bundles"
$list = Join-Path $env:TEMP "gcg_missing.txt"
$archive = Join-Path $env:TEMP "gcg_missing.tar"
[IO.File]::WriteAllLines($list, [string[]]$missing, (New-Object Text.UTF8Encoding $false))
& "$env:SystemRoot\System32\tar.exe" -cf $archive -C $bundles -T $list
& $adb -s $Device push $archive "$tmp/gcg_missing.tar" 2>&1 | Out-Null
# Only the new files change owner: the game owns folders like DecodedBanks and must keep them.
RunOnDevice @"
cd $files
tar -xf $tmp/gcg_missing.tar
tar -tf $tmp/gcg_missing.tar | while read -r f; do
  chown 2000:2000 "`$f"
  chmod 666 "`$f"
  d=`$(dirname "`$f")
  [ "`$d" = "." ] || chown 2000:2000 "`$d"
done
rm $tmp/gcg_missing.tar
"@ | Out-Null
Remove-Item $list, $archive

$left = ListMissing
if ($left.Count -gt 0) { Write-Warning "$($left.Count) bundles still missing, first: $($left[0])" }
else { Write-Host "device assets installed" }
