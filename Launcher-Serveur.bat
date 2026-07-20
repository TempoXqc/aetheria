@echo off
title Launcher SERVEUR Aetheria
cd /d "%~dp0"

rem Deja lance ? On rouvre juste la page.
powershell -NoProfile -Command "try { $null = Invoke-WebRequest http://localhost:5181 -UseBasicParsing -TimeoutSec 1; exit 0 } catch { exit 1 }" >nul 2>&1
if not errorlevel 1 (
    start msedge --app=http://localhost:5181 2>nul || start http://localhost:5181
    exit /b 0
)

rem Le launcher SERVEUR compile dans SON propre dossier (artifacts-host) : le launcher
rem joueur (port 5180) verrouille artifacts\bin\Launcher — plus jamais de collision
rem "The file is locked by .NET Host" entre les deux.
dotnet run -c Release --project tools\Launcher -p:ArtifactsPath=artifacts-host -- --host
if errorlevel 1 (
    echo.
    echo ======================================================
    echo   Le launcher serveur n'a pas pu demarrer ^(voir plus haut^).
    echo ======================================================
    pause
)
