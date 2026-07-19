@echo off
setlocal
title Preparation du build - Aetheria
echo.
echo   Ce script cree le fichier servers.txt pointe sur TON IP publique.
echo   Place-le dans le dossier du build (a cote du .exe) et lance-le,
echo   OU lance-le ici puis copie le servers.txt genere dans le build.
echo.

set IP=
for /f "delims=" %%i in ('curl -s ifconfig.me') do set IP=%%i
if "%IP%"=="" (
    echo   Impossible de detecter l'IP publique automatiquement.
    set /p IP=  Entre ton IP publique a la main :
)

echo Zul'jin^|%IP%:27015> "%~dp0servers.txt"
echo.
echo   servers.txt cree : Zul'jin ^| %IP%:27015
echo   Tes amis verront le royaume " Zul'jin " directement dans leur liste.
echo.
pause
