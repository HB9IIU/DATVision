"""Build DATVision release assets. Requires Python, .NET SDK, MSYS2 and Rust."""
import argparse
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import urllib.request
import urllib.error
import zipfile


def run(*args, **kwargs):
    subprocess.run(args, check=True, **kwargs)


def zip_tree(folder, target, prefix):
    if not (folder / 'DATVision.exe').is_file():
        raise FileNotFoundError(f'Portable application missing from staging: {folder}')
    with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for file in sorted(folder.rglob('*')):
            relative = file.relative_to(folder)
            # A user may have launched the staging copy between rebuilds. Only
            # publish known distribution files, never its new settings or logs.
            allowed = (relative.parts[0] in ('build', 'runtime', 'licenses')
                or str(relative) in ('DATVision.exe', 'README.txt', 'LICENSE', 'THIRD-PARTY-PACKAGES.json'))
            if file.is_file() and allowed:
                archive.write(file, str(Path(prefix) / relative))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--version', default='0.1.0-preview.1')
    parser.add_argument('--msys-root', default='C:/msys64')
    parser.add_argument('--skip-native-build', action='store_true')
    parser.add_argument('--resume', action='store_true', help='Resume a failed local package build')
    args = parser.parse_args()
    if not re.fullmatch(r'\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?', args.version):
        raise ValueError('Version must be a semantic version')
    root = Path(__file__).resolve().parents[2]
    os.chdir(root)
    msys = Path(args.msys_root)
    tools = msys / 'ucrt64/bin'
    output = root / 'dist' / args.version
    if output.exists() and not args.resume:
        raise FileExistsError(f'Use a new version or move the existing output: {output}')
    output.mkdir(parents=True, exist_ok=args.resume)
    stage = root / 'build/release-staging' / args.version / f'DATVision-{args.version}-win-x64'
    stage.mkdir(parents=True, exist_ok=args.resume)
    if not args.skip_native_build:
        run('powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(root / 'scripts/windows/build.ps1'), '-MsysRoot', str(msys))
    native_cache = root / 'build/windows/CMakeCache.txt'
    if native_cache.exists() and 'DATVRX_NATIVE:BOOL=ON' in native_cache.read_text():
        raise RuntimeError('Portable builds require DATVRX_NATIVE=OFF. Reconfigure the native build first.')
    run('dotnet', 'publish', str(root / 'ui/DatvRx.Player/DatvRx.Player.csproj'), '-c', 'Release',
        '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:AssemblyName=DATVision', '-p:Product=DATVision',
        f'-p:Version={args.version}', '--artifacts-path', str(root / 'build/portable-dotnet'),
        '-o', str(stage), '--source', 'https://api.nuget.org/v3/index.json')
    # PDBs are build artifacts, not required at runtime.
    for pdb in stage.glob('*.pdb'):
        pdb.unlink()
    binaries = stage / 'build/windows'
    runtime = stage / 'runtime/bin'
    binaries.mkdir(parents=True, exist_ok=True)
    runtime.mkdir(parents=True, exist_ok=True)
    seeds = [tools / 'ffplay.exe']
    for name in ('datvrx.exe', 'rtl_iq.exe', 'rtl_sweep.exe'):
        source = root / 'build/windows' / name
        shutil.copy2(source, binaries / name)
        seeds.append(source)

    def imports(path):
        result = subprocess.check_output([str(tools / 'objdump.exe'), '-p', str(path)], text=True)
        return re.findall(r'DLL Name:\s*(\S+)', result)

    seen = {}
    todo = seeds
    while todo:
        batch = [p for p in todo if p.name.lower() not in seen]
        for file in batch:
            seen[file.name.lower()] = file
        todo = []
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as executor:
            for file, dependencies in zip(batch, executor.map(imports, batch)):
                for name in dependencies:
                    if (tools / name).exists():
                        if name.lower() not in seen:
                            todo.append(tools / name)
                    elif not name.lower().startswith(('api-ms-', 'ext-ms-')) and not (Path(os.environ['WINDIR']) / 'System32' / name).exists():
                        raise FileNotFoundError(f'{file.name} requires missing {name}')
    bundled = [p for p in seen.values() if p.parent == tools]
    for file in bundled:
        shutil.copy2(file, runtime / file.name)
    print(f'Bundled ffplay and {len(bundled)-1} runtime DLLs', flush=True)

    # Record exact installed MSYS2 package versions and the corresponding sources.
    owners = {}
    packages = {}
    for entry in (msys / 'var/lib/pacman/local').iterdir():
        if not (entry / 'files').exists():
            continue
        fields = {}
        chunks = (entry / 'desc').read_text(encoding='utf-8').split('\n\n')
        for chunk in chunks:
            lines = chunk.strip().splitlines()
            if len(lines) > 1:
                fields[lines[0].strip('%')] = lines[1:]
        for line in (entry / 'files').read_text(encoding='utf-8').splitlines():
            if line.startswith('ucrt64/bin/'):
                owners[Path(line).name.lower()] = (entry, fields)
    for file in bundled:
        if file.name.lower() not in owners:
            raise RuntimeError(f'Cannot identify source package for {file.name}')
        entry, fields = owners[file.name.lower()]
        name = fields['NAME'][0]
        packages[name] = dict(name=name, version=fields['VERSION'][0], base=fields['BASE'][0],
            license=fields.get('LICENSE', []), homepage=fields.get('URL', [''])[0])
    (output / 'package-inputs.json').write_text(json.dumps(list(packages.values()), indent=2), encoding='utf-8')
    sources = output / 'third-party-sources'
    sources.mkdir(exist_ok=True)
    cache = root / 'build/release-source-cache'
    cache.mkdir(parents=True, exist_ok=True)

    def fetch_source(package):
        for extension in ('zst', 'xz', 'gz'):
            filename = f"{package['base']}-{package['version']}.src.tar.{extension}"
            url = 'https://repo.msys2.org/mingw/sources/' + filename
            cached = cache / filename
            try:
                if not cached.exists():
                    temporary = cached.with_suffix('.download')
                    with urllib.request.urlopen(url, timeout=60) as response, temporary.open('wb') as out:
                        shutil.copyfileobj(response, out)
                    temporary.replace(cached)
                break
            except urllib.error.HTTPError as error:
                if error.code != 404 or extension == 'gz':
                    raise RuntimeError(f'Source unavailable: {url}') from error
        shutil.copy2(cached, sources / filename)
        package['source'] = url
        package['source_sha256'] = hashlib.sha256(cached.read_bytes()).hexdigest()
        return package

    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as executor:
        manifest = list(executor.map(fetch_source, packages.values()))
    (stage / 'THIRD-PARTY-PACKAGES.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    # Preserve every installed upstream copyright/license notice (including names
    # different from the package name), plus the application's GPL text.
    shutil.copytree(msys / 'ucrt64/share/licenses', stage / 'licenses/MSYS2', dirs_exist_ok=True)
    shutil.copy2(root / 'LICENSE', stage / 'LICENSE')
    nuget = Path(os.environ.get('NUGET_PACKAGES', str(Path.home() / '.nuget/packages')))
    for package in ('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64'):
        package_root = nuget / package
        versions = sorted(package_root.iterdir(), key=lambda p: tuple(int(n) for n in p.name.split('.')))
        latest = versions[-1]
        for file in latest.iterdir():
            if file.name.upper() in ('LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT'):
                target = stage / 'licenses' / package / file.name
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(file, target)
    (stage / 'README.txt').write_text('''DATVision by HB9IIU — portable Windows x64 preview

Extract the entire ZIP to a writable folder, then double-click DATVision.exe.
Keep the build, runtime and licenses subfolders alongside the EXE.
No .NET or MSYS2 installation is required. Windows 10/11 x64.
An RTL-SDR with the WinUSB driver is required for live reception. If necessary,
use Zadig (https://zadig.akeo.ie/) to install WinUSB for the RTL-SDR device's
Bulk-In Interface 0. Select the correct device, not another USB peripheral.
For QO-100 you still need the dish, LNB/downconverter and its power supply.

Startup selects QO-100 beacon 10491.500 MHz / 1500 kS/s and BATC spectrum.
Settings selects LOCAL scanning, RF gain, Effective LO and beacon calibration.
BATC needs Internet access; LOCAL reception/scanning does not.
Recordings: Record/<callsign>/DATV-<timestamp>.ts, alongside DATVision.exe.
Settings and diagnostic logs also stay in this extracted folder.
UDP streaming is disabled by default. Enable it in Settings > UDP streaming,
click OK, then Apply and save. Paste Copy VLC address into VLC on a LAN PC.
Allow VLC through that PC's private-network firewall; Wi-Fi may block multicast.
H.266/VVC decoding is included; performance depends on the stream and PC.

This is a preview, unsigned build. No installer or automatic updater.
Source and third-party source archives accompany this ZIP in the release.
See LICENSE, licenses and THIRD-PARTY-PACKAGES.json for notices and versions.
''', encoding='utf-8')
    shutil.copy2(root / 'assets/branding/datvision-github.png', output / 'DATVision-github.png')
    # Capture the actual working tree, including edits not committed yet, without
    # recordings, logs, settings, build output, credentials or .git history.
    source_zip = output / f'DATVision-{args.version}-source.zip'
    files = subprocess.check_output(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z']).decode().split('\0')
    with zipfile.ZipFile(source_zip, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for relative in sorted(set(files)):
            if relative and (root / relative).is_file():
                archive.write(root / relative, str(Path(f'DATVision-{args.version}-source') / relative))
    zip_tree(stage, output / f'{stage.name}.zip', stage.name)
    # Source packages are already compressed; avoid spending time recompressing.
    with zipfile.ZipFile(output / f'DATVision-{args.version}-third-party-sources.zip', 'w', zipfile.ZIP_STORED) as archive:
        for file in sorted(sources.iterdir()):
            archive.write(file, 'third-party-sources/' + file.name)
    assets = sorted(output.glob('*.zip'))
    (output / 'SHA256SUMS.txt').write_text(''.join(f'{hashlib.sha256(file.read_bytes()).hexdigest()}  {file.name}\n' for file in assets), encoding='utf-8')
    print(f'Release assets: {output}', flush=True)


if __name__ == '__main__':
    main()
