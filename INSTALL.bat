@echo off
REM ============================================================================
REM  Girl Cafe Gun - Offline : one-click installer
REM  Double-click this. It installs the APK, the OBB, syncs the game bundles,
REM  and launches the game on your emulator/phone. Nothing goes online.
REM
REM  Need to point it at a hosted asset pack? Run from a terminal instead:
REM     INSTALL.bat -AssetPackUrl https://your-host/gcg_assets.zip
REM  Pick a specific device:
REM     INSTALL.bat -Device 127.0.0.1:16448
REM ============================================================================
setlocal
echo.
echo  Girl Cafe Gun - Offline installer
echo  ---------------------------------
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1" %*
echo.
echo  (press any key to close)
pause >nul
