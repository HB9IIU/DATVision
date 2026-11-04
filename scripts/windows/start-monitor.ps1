param([string]$MsysRoot = 'C:\msys64')
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
foreach ($relative in @('build/windows/monitor/DatvRx.Player.exe', 'build/windows/datvrx.exe', 'build/windows/rtl_iq.exe', 'build/windows/rtl_sweep.exe')) {
    if (!(Test-Path -LiteralPath (Join-Path $projectRoot $relative))) { throw "Missing $relative. See docs/windows.md for build instructions." }
}
if (Get-Process DatvRx.Player -ErrorAction SilentlyContinue) {
    throw 'Close the current DATV player before opening the Windows monitor.'
}
if (Get-Process rtl_sdr,rtl_iq,rtl_sweep,pluto_iq,pluto_sweep,datvrx -ErrorAction SilentlyContinue) {
    throw 'An SDR capture is already running. Close it before opening the monitor.'
}
$env:DATVRX_MSYS_ROOT = $MsysRoot
Start-Process -FilePath (Join-Path $projectRoot 'build/windows/monitor/DatvRx.Player.exe') -ArgumentList @('"' + $projectRoot + '"') -WorkingDirectory $projectRoot | Out-Null
