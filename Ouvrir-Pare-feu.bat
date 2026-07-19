@echo off
title Pare-feu Windows - port Aetheria
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
netsh advfirewall firewall add rule name="Aetheria UDP 27015" dir=in action=allow protocol=UDP localport=27015
echo.
echo   Port UDP 27015 autorise dans le pare-feu Windows.
echo.
pause
