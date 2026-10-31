param([string]$MsysRoot = 'C:\msys64')
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$player = Join-Path $MsysRoot 'ucrt64/bin/ffplay.exe'
if (!(Test-Path -LiteralPath $player)) { throw "MSYS2 ffplay not found at $player" }
if (Get-Process rtl_sdr,rtl_iq,datvrx -ErrorAction SilentlyContinue) {
    throw 'A receiver or RTL-SDR capture is already running. Close it before starting the beacon.'
}
$launcher = Join-Path $PSScriptRoot 'live-beacon-pipeline.cmd'
$env:DATVRX_MSYS_ROOT = $MsysRoot
$process = Start-Process -FilePath $env:ComSpec -ArgumentList @('/d', '/c', '"' + $launcher + '"') -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
Write-Host "Live beacon started (launcher PID $($process.Id)). Video may take several seconds to appear."
Write-Host 'Close the video window or press Q to stop. F toggles full screen; M toggles mute.'
Write-Host "Diagnostics: $projectRoot/test-output/live-beacon-*.log"
