@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\windows\start-player.ps1" -RemotePi
if errorlevel 1 pause
