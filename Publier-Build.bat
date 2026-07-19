@echo off
setlocal
title Publier une mise a jour - Aetheria
echo.
echo   Publie un build Unity dans un canal du launcher.
echo.
set /p SRC=  Dossier du build Unity (glisse le dossier ici) :
set /p CHAN=  Canal (prod ou staging) :
set /p VER=  Version (ex 0.43.0) :
set /p NOTES=  Notes de mise a jour (une ligne) :
cd /d "%~dp0"
dotnet run -c Release --project tools\PatchServer -- publish %SRC% %CHAN% %VER% %NOTES%
echo.
pause
