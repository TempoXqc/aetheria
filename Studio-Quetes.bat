@echo off
title Studio de Quetes - Aetheria
echo.
echo   Lancement du Studio de Quetes...
echo   Le navigateur va s'ouvrir sur http://localhost:5178
echo.
cd /d "%~dp0"
dotnet run --project tools\QuestStudio
pause
