@echo off
title Serveur de patchs Aetheria
cd /d "%~dp0"

rem Deja lance ? Inutile d'en demarrer un deuxieme.
powershell -NoProfile -Command "try { $null = Invoke-WebRequest http://localhost:27080/news -UseBasicParsing -TimeoutSec 1; exit 0 } catch { exit 1 }" >nul 2>&1
if not errorlevel 1 (
    echo   Le serveur de patchs tourne deja — rien a faire.
    pause
    exit /b 0
)

echo.
echo   Serveur de mises a jour (port TCP 27080, ouvert via UPnP).
echo   Les launchers de tes amis verifient ici s'il y a du nouveau.
echo   Laisse cette fenetre ouverte.
echo.
dotnet run -c Release --project tools\PatchServer
pause
