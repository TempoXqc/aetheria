@echo off
title Serveur Aetheria - Zul'jin
echo.
echo   Lancement du royaume Zul'jin...
echo   (le serveur ouvre le port sur ta box automatiquement via UPnP
echo    et affiche ton IP PUBLIQUE a donner a tes amis)
echo.
cd /d "%~dp0"
dotnet run -c Release --project src\Aetheria.Server -- --name "Zul'jin"
pause
