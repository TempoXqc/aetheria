@echo off
title Serveur Aetheria - Zul'jin TTS (test)
echo.
echo   Lancement du royaume de TEST « Zul'jin TTS » (port 27016)...
echo   C'est LE serveur partage pour developper : toi depuis Unity et
echo   tes testeurs depuis le canal TTS du launcher jouez au meme endroit.
echo.
cd /d "%~dp0"
dotnet run -c Release --project src\Aetheria.Server -- --name "Zul'jin TTS" --port 27016 --state state\aetheria-tts.json
pause
