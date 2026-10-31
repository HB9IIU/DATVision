@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\windows\start-player.ps1"
if errorlevel 1 pause
