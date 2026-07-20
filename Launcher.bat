@echo off
title Launcher Aetheria
cd /d "%~dp0"

rem Deja lance ? On rouvre juste la page au lieu de recompiler par-dessus.
powershell -NoProfile -Command "try { $null = Invoke-WebRequest http://localhost:5180 -UseBasicParsing -TimeoutSec 1; exit 0 } catch { exit 1 }" >nul 2>&1
if not errorlevel 1 (
    start msedge --app=http://localhost:5180 2>nul || start http://localhost:5180
    exit /b 0
)

rem Le launcher JOUEUR compile dans SON propre dossier (artifacts-player) : ni le
rem launcher serveur ni un ancien processus ne peuvent verrouiller sa DLL.
dotnet run -c Release --project tools\Launcher -p:ArtifactsPath=artifacts-player
if errorlevel 1 (
    echo.
    echo ======================================================
    echo   Le launcher n'a pas pu demarrer ^(voir plus haut^).
    echo ======================================================
    pause
)
