@echo off
setlocal enabledelayedexpansion
title Publier une mise a jour - Aetheria
echo.
echo   Publie un build Unity dans un canal du launcher.
echo   (astuce : CLIC DROIT dans cette fenetre = coller un chemin copie)
echo.

rem Le dossier peut venir : 1) d'un argument, 2) du dernier utilise, 3) de la saisie.
set "SRC=%~1"
if "%SRC%"=="" if exist "%~dp0derniere-publication.txt" set /p SRC=<"%~dp0derniere-publication.txt"

if not "%SRC%"=="" (
    set /p IN=  Dossier du build [Entree = !SRC!] :
) else (
    set /p IN=  Dossier du build (colle le chemin ici) :
)
if not "!IN!"=="" set "SRC=!IN!"
set "SRC=!SRC:"=!"

if not exist "!SRC!" (
    echo.
    echo   Dossier introuvable : "!SRC!"
    pause
    exit /b 1
)

set /p CHAN=  Canal (prod ou staging) [prod] :
if "!CHAN!"=="" set CHAN=prod
set /p VER=  Version (ex 0.43.0) :
set /p NOTES=  Notes de mise a jour (une ligne) :

cd /d "%~dp0"
dotnet run -c Release --project tools\PatchServer -- publish "!SRC!" !CHAN! !VER! !NOTES!
if not errorlevel 1 echo !SRC!>"%~dp0derniere-publication.txt"
echo.
pause
