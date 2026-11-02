param(
    [string]$MsysRoot = 'C:\msys64',
    [string]$RustToolchain = 'stable-x86_64-pc-windows-gnu'
)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$toolBin = Join-Path $MsysRoot 'ucrt64/bin'
foreach ($tool in @('cmake.exe', 'ninja.exe', 'gcc.exe', 'g++.exe')) {
    if (!(Test-Path -LiteralPath (Join-Path $toolBin $tool))) {
        throw "Missing $tool in $toolBin. See docs/building.md."
    }
}
Get-Command cargo -ErrorAction Stop | Out-Null
$env:RUSTUP_TOOLCHAIN = $RustToolchain
& rustc --version
if ($LASTEXITCODE -ne 0) { throw 'Rust compiler is unavailable. Repair the Rust toolchain; see docs/building.md.' }
$env:PATH = "$toolBin;$env:PATH"
$env:CARGO_TARGET_X86_64_PC_WINDOWS_GNU_LINKER = Join-Path $toolBin 'gcc.exe'
$buildDir = Join-Path $projectRoot 'build/windows'
& (Join-Path $toolBin 'cmake.exe') -S $projectRoot -B $buildDir -G Ninja '-DCMAKE_BUILD_TYPE=Release' '-DDATVRX_RTL=ON' "-DCMAKE_C_COMPILER=$toolBin/gcc.exe" "-DCMAKE_CXX_COMPILER=$toolBin/g++.exe"
if ($LASTEXITCODE -ne 0) { throw 'CMake configuration failed.' }
& (Join-Path $toolBin 'cmake.exe') --build $buildDir --parallel 2
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host "Receiver: $buildDir/datvrx.exe"
