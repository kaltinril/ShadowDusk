# Native PowerShell launcher for build-dxc-wasm.ps1.
#
# Loads a PRE-CAPTURED MSVC host environment (msvc-env.txt) into this session, then
# runs build-dxc-wasm.ps1. Stage 0 (host tablegen) needs MSVC; emscripten (Stages 1-2)
# uses emsdk's own clang. We load a captured env file rather than calling vcvars64.bat
# here because vcvars64 fails to fully initialize when this script is launched as a
# DETACHED background process (it produces a partial env with no MSVC bin) — capturing
# the env once from an interactive shell and replaying it is reliable in any context.
#
# To (re)generate msvc-env.txt from an interactive shell:
#   $vc='C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat'
#   cmd /c "call `"$vc`" 1>/dev/null 2>/dev/null & set > C:\git\ShadowDusk\.wasm-build\msvc-env.txt"
#
#   pwsh -NoProfile -File .\Invoke-DxcWasmBuild.ps1 [-SkipHostTblgen] [-SkipLib] [-ConfigureOnly]
param([Parameter(ValueFromRemainingArguments=$true)] $Rest)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here

$envFile = Join-Path $here 'msvc-env.txt'
if (-not (Test-Path $envFile)) {
    throw "msvc-env.txt not found at $envFile. Regenerate it from an interactive shell (see header)."
}
$loaded = 0
foreach ($line in (Get-Content -LiteralPath $envFile)) {
    $eq = $line.IndexOf('=')
    if ($eq -gt 0) {
        Set-Item -Path "env:$($line.Substring(0,$eq))" -Value $line.Substring($eq+1) -ErrorAction SilentlyContinue
        $loaded++
    }
}
$cl = Get-Command cl.exe -ErrorAction SilentlyContinue
if (-not $cl) { throw "cl.exe not on PATH after loading msvc-env.txt ($loaded vars). Regenerate msvc-env.txt." }
Write-Host "MSVC host toolchain loaded from msvc-env.txt ($loaded vars): $($cl.Source)"

& (Join-Path $here 'build-dxc-wasm.ps1') @Rest
exit $LASTEXITCODE
