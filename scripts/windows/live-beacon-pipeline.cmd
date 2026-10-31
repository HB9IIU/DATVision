@echo off
cd /d "%~dp0..\.."
if not defined DATVRX_MSYS_ROOT set "DATVRX_MSYS_ROOT=C:\msys64"
set "PATH=%DATVRX_MSYS_ROOT%\ucrt64\bin;%PATH%"
if not exist test-output mkdir test-output
"build\windows\rtl_iq.exe" 741500000 2560000 40.2 2>test-output\live-beacon-radio.log | "build\windows\datvrx.exe" - --rate 2.56 --format cu8 --rs 1.5 --std 2 --roll 0.25 --no-realtime 2>test-output\live-beacon-receiver.log | ffplay.exe -hide_banner -loglevel warning -nostats -autoexit -alwaysontop -window_title "QO-100 Beacon - A71A - LIVE" -x 640 -y 360 -left 60 -top 60 -volume 30 -f mpegts -i - 2>test-output\live-beacon-player.log
