@echo off
title Pare-feu Windows - ports Aetheria
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo   Ce script doit etre lance EN ADMINISTRATEUR :
    echo   clic droit sur le fichier - "Executer en tant qu'administrateur".
    echo.
    pause
    exit /b 1
)

netsh advfirewall firewall delete rule name="Aetheria UDP 27015" >nul 2>&1
netsh advfirewall firewall delete rule name="Aetheria UDP 27016" >nul 2>&1
netsh advfirewall firewall delete rule name="Aetheria TCP 27080" >nul 2>&1
netsh advfirewall firewall add rule name="Aetheria UDP 27015" dir=in action=allow protocol=UDP localport=27015
netsh advfirewall firewall add rule name="Aetheria UDP 27016" dir=in action=allow protocol=UDP localport=27016
netsh advfirewall firewall add rule name="Aetheria TCP 27080" dir=in action=allow protocol=TCP localport=27080
echo.
echo   Ports autorises : jeu 27015 (Zul'jin), 27016 (TTS), patchs 27080.
echo.
pause
