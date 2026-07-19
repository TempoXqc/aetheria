@echo off
title Launcher Aetheria
cd /d "%~dp0"
dotnet run -c Release --project tools\Launcher
