@echo off
title Serveur de patchs Aetheria
echo.
echo   Serveur de mises a jour (port TCP 27080, ouvert via UPnP).
echo   Les launchers de tes amis verifient ici s'il y a du nouveau.
echo   Laisse cette fenetre ouverte.
echo.
cd /d "%~dp0"
dotnet run -c Release --project tools\PatchServer
pause
