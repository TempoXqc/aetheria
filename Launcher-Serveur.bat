@echo off
title Launcher SERVEUR Aetheria
cd /d "%~dp0"

rem Deja lance ? On rouvre juste la page.
powershell -NoProfile -Command "try { $null = Invoke-WebRequest http://localhost:5181 -UseBasicParsing -TimeoutSec 1; exit 0 } catch { exit 1 }" >nul 2>&1
if not errorlevel 1 (
    start msedge --app=http://localhost:5181 2>nul || start http://localhost:5181
    exit /b 0
)

rem Nettoyage d'un dossier laisse par d'anciennes versions DANS le projet (il cassait la
rem compilation avec des attributs en double) — sans effet s'il n'existe pas.
if exist tools\Launcher\artifacts-player rmdir /s /q tools\Launcher\artifacts-player
if exist tools\Launcher\artifacts-host rmdir /s /q tools\Launcher\artifacts-host

dotnet run -c Release --project tools\Launcher -- --host
