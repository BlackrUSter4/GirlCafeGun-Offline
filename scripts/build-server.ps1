<#
  build-server.ps1 — publish the embedded server as a native Android (arm64) build.

  Produces a self-contained .NET 10 build (JIT runtime bundled, NOT AOT — the
  server uses reflection/dynamic JSON that AOT can't handle). Output goes to
  server/src/SssjServer.App/bin/Release/net10.0/linux-bionic-arm64/publish and is
  consumed by build-apk.ps1.

  Requirements: .NET 10 SDK. (The SDK brings its own bundled NDK for the
  linux-bionic target; no separate NDK needed for THIS step.)

  Usage: powershell -ExecutionPolicy Bypass -File build-server.ps1
#>
[CmdletBinding()] param()
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "server\src\SssjServer.App\SssjServer.App.csproj"
if (-not (Test-Path $proj)) { throw "Server source not found at $proj" }
if (-not (Test-Path (Join-Path $root "server\setting"))) {
    Write-Warning "server/setting (the game config tables) is missing. The server needs it to run; it ships in the asset pack or is extracted from your game. Build will still publish, but the server won't start without it."
}
Write-Host "Publishing server (self-contained linux-bionic-arm64, JIT) ..." -ForegroundColor Cyan
dotnet publish -c Release -r linux-bionic-arm64 --self-contained -p:PublishAot=false "$proj"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
$pub = Join-Path $root "server\src\SssjServer.App\bin\Release\net10.0\linux-bionic-arm64\publish"
Write-Host "Published to: $pub" -ForegroundColor Green
