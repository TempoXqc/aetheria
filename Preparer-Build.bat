@echo off
setlocal
title Preparation du build - Aetheria
echo.
echo   Ce script cree le fichier servers.txt du royaume Zul'jin avec
echo   TOUTES ses routes : IP publique (amis), IP locale (ton reseau),
echo   et localhost (toi, sur le PC du serveur). Le jeu choisit tout
echo   seul la route qui repond — le meme build marche pour tout le monde.
echo.

set PUBIP=
for /f "delims=" %%i in ('curl -s ifconfig.me') do set PUBIP=%%i
if "%PUBIP%"=="" (
    echo   Impossible de detecter l'IP publique automatiquement.
    set /p PUBIP=  Entre ton IP publique a la main :
)

set LANIP=
for /f "delims=" %%i in ('powershell -NoProfile -Command "(Get-NetIPAddress -AddressFamily IPv4 ^| Where-Object {$_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*'} ^| Select-Object -First 1).IPAddress"') do set LANIP=%%i

if "%LANIP%"=="" (
    echo Zul'jin^|%PUBIP%:27015^|127.0.0.1:27015> "%~dp0servers.txt"
) else (
    echo Zul'jin^|%PUBIP%:27015^|%LANIP%:27015^|127.0.0.1:27015> "%~dp0servers.txt"
)

echo.
echo   servers.txt cree :
type "%~dp0servers.txt"
echo.
echo   Copie ce fichier dans le dossier du build, a cote du .exe,
echo   pour TOI comme pour tes amis — le meme fichier sert a tous.
echo.
pause
