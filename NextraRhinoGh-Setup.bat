@echo off
rem NextraRhinoGh setup: double-click this file. It installs Node.js, which the AI apps use to start the Rhino bridge.
rem Re-running is safe: if Node.js 18 or newer is already installed, nothing is changed.
setlocal EnableExtensions
title NextraRhinoGh setup
set "NODE_PAGE=https://nodejs.org/en/download"

echo.
echo  NextraRhinoGh setup
echo  ===================
echo.
echo  [1/3] Looking for Node.js ...
call :check_node
if "%NODE_OK%"=="1" goto done_ok

echo.
echo  [2/3] Installing Node.js LTS with winget ...
where winget >nul 2>nul
if errorlevel 1 (
  echo        winget is not available on this computer, trying the direct download.
  goto direct
)
winget install --id OpenJS.NodeJS.LTS -e --silent --accept-source-agreements --accept-package-agreements
call :check_node
if "%NODE_OK%"=="1" goto done_new
echo        winget did not finish the installation, trying the direct download.

:direct
echo.
echo  [3/3] Downloading the Node.js LTS installer from nodejs.org ...
set "NODE_MSI=%TEMP%\nextra-node-lts.msi"
set "NODE_ARCH=x64"
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" set "NODE_ARCH=arm64"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12;" ^
  "$all=Invoke-RestMethod 'https://nodejs.org/dist/index.json'; $v=@($all | Where-Object { $_.lts })[0].version;" ^
  "$u=\"https://nodejs.org/dist/$v/node-$v-%NODE_ARCH%.msi\"; Write-Host \"       $u\";" ^
  "Invoke-WebRequest -UseBasicParsing $u -OutFile '%NODE_MSI%'"
if errorlevel 1 goto manual
echo        Installing (Windows asks for permission) ...
msiexec /i "%NODE_MSI%" /passive /norestart
del "%NODE_MSI%" >nul 2>nul
call :check_node
if "%NODE_OK%"=="1" goto done_new

:manual
echo.
echo  Node.js could not be installed automatically.
echo  Download the LTS installer (Windows Installer .msi) from:
echo.
echo      %NODE_PAGE%
echo.
echo  Run it with the default options, then restart your AI app.
echo  Opening the page in your browser ...
start "" "%NODE_PAGE%"
echo.
pause
exit /b 1

:done_new
echo.
echo  Node.js %NODE_VER% is installed.
echo  Restart your AI app (ChatGPT or Claude) so it finds Node.js.
goto next

:done_ok
echo        Node.js %NODE_VER% is already installed. Nothing to do.

:next
echo.
echo  Next: install the Nextra-RhinoGh plug-in in Rhino (PackageManager) and the
echo  NextraRhinoGh plug-in in your AI app. Guide: https://github.com/nextra-studio/nextra-rhinogh#install
echo.
pause
exit /b 0

rem Sets NODE_OK=1 and NODE_VER when Node.js 18 or newer is found, on PATH or in the default install folder
rem (a fresh install is not on this window's PATH yet).
:check_node
set "NODE_OK=0"
set "NODE_VER="
set "NODE_EXE=node"
where node >nul 2>nul
if errorlevel 1 (
  if exist "%ProgramFiles%\nodejs\node.exe" (set "NODE_EXE=%ProgramFiles%\nodejs\node.exe") else (goto :eof)
)
for /f "delims=" %%v in ('"%NODE_EXE%" --version 2^>nul') do set "NODE_VER=%%v"
if not defined NODE_VER goto :eof
for /f "tokens=1 delims=." %%m in ("%NODE_VER:v=%") do set "NODE_MAJOR=%%m"
if %NODE_MAJOR% GEQ 18 (set "NODE_OK=1") else (echo        Found Node.js %NODE_VER%, but 18 or newer is needed.)
goto :eof
