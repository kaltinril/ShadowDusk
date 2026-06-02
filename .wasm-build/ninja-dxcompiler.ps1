# Resume/run `ninja dxcompiler` against the already-configured build-wasm tree, with the
# MSVC host env loaded (the host tablegen + any hctgen codegen steps need it). Separate
# from the staged build-dxc-wasm.ps1 so the validated configure is NOT re-run (re-running
# emcmake on top of a live build tree collides with ninja's log). Resumes from cached .o.
$ErrorActionPreference = 'Stop'
$here  = 'C:\git\ShadowDusk\.wasm-build'
$bdir  = Join-Path $here 'dxc-src\build-wasm'
foreach ($line in (Get-Content (Join-Path $here 'msvc-env.txt'))) {
    $eq = $line.IndexOf('='); if ($eq -gt 0) { Set-Item "env:$($line.Substring(0,$eq))" $line.Substring($eq+1) -EA SilentlyContinue }
}
$ninja = (Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\2022\*\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe' | Select-Object -First 1).FullName
$env:EMSDK = Join-Path $here 'emsdk'; $env:EM_CONFIG = Join-Path $env:EMSDK '.emscripten'
Write-Host "ninja: $ninja"; Write-Host "build dir: $bdir"
& $ninja -C $bdir dxcompiler
exit $LASTEXITCODE
