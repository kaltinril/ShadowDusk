#Requires -Version 7
<#
.SYNOPSIS
    Compiles all fixture shaders with MonoGame's mgfxc to produce golden reference output.

.DESCRIPTION
    Finds mgfxc.exe in the NuGet cache, then compiles every .fx file in
    tests/fixtures/shaders/ for each target profile. Output goes to
    tests/fixtures/golden/<Profile>/<shader>.mgfx.

    Run this before ShadowDusk is built to capture ground-truth output.
    After ShadowDusk is built, compile the same files with ShadowDusk and
    diff against these golden outputs to verify correctness.

.PARAMETER Profiles
    Which MonoGame profiles to compile for. Defaults to DirectX_11 and OpenGL.

.PARAMETER ShaderDir
    Path to shader fixture directory. Defaults to tests/fixtures/shaders relative to repo root.

.PARAMETER GoldenDir
    Path to golden output directory. Defaults to tests/fixtures/golden relative to repo root.

.PARAMETER MgfxcVersion
    Which mgfxc to compile the goldens with. Defaults to the dotnet-mgcb version pinned
    in .config/dotnet-tools.json. See the ORACLE PIN note below before changing it.

.EXAMPLE
    .\tools\compile-fixtures.ps1
    .\tools\compile-fixtures.ps1 -Profiles DirectX_11
#>
param(
    [string[]] $Profiles  = @("DirectX_11", "OpenGL"),
    [string]   $ShaderDir = $null,
    [string]   $GoldenDir = $null,
    [string]   $MgfxcVersion = $null
)

$RepoRoot  = Split-Path $PSScriptRoot -Parent
# NOTE: `??` is wrong for these - a [string] parameter defaulted to $null arrives as
# an EMPTY STRING, which is not null, so `$ShaderDir ?? ...` kept '' and the script
# silently found 0 shaders on a no-argument run.
if ([string]::IsNullOrEmpty($ShaderDir)) { $ShaderDir = Join-Path $RepoRoot "tests\fixtures\shaders" }
if ([string]::IsNullOrEmpty($GoldenDir)) { $GoldenDir = Join-Path $RepoRoot "tests\fixtures\golden" }

# ---------------------------------------------------------------------------
# ORACLE PIN (Phase 52 Area C, evidence recorded 2026-07-28)
#
# The goldens are canonical MGFX **v10**. Which mgfxc produces them is a pinned
# decision, NOT "whatever is newest on this machine" - that heuristic was wrong
# in two different ways at the same time:
#
#   * mgfxc 3.8.5 emits MGFX **v11** (version byte 11; PR #8813 adds a per-shader
#     SourceFile + Entrypoint string). Every corpus file came out 58-2232 bytes
#     larger and NOT ONE of 46 matched its golden, on either profile. Faithful
#     for a 3.8.5 consumer, useless as the v10 oracle.
#   * the dotnet-mgcb-editor-windows **3.8.4.1** package ships mgfxc.exe WITHOUT
#     SharpDX.D3DCompiler.dll, so it throws "Could not load file or assembly" on
#     every shader. The old highest-version-wins probe selected exactly that
#     binary, so this script compiled 0/46 and reported them all as FAIL.
#
# Resolve from the dotnet-mgcb package instead (the one .config/dotnet-tools.json
# already pins, and whose mgfxc payload is intact), at an explicit version, and
# assert the container version of what comes out. Verified byte-for-byte: mgfxc
# 3.8.2.1105 and 3.8.4.1 both reproduce all 46 committed goldens exactly, on both
# OpenGL and DirectX_11.
#
# Changing this pin means regenerating goldens; do it deliberately, with a
# recorded corpus diff, not as a side effect of installing a newer tool.
# ---------------------------------------------------------------------------
$ExpectedMgfxVersion = 10

if (-not $MgfxcVersion) {
    $toolsJson = Join-Path $RepoRoot ".config\dotnet-tools.json"
    if (-not (Test-Path $toolsJson)) { Write-Error "Not found: $toolsJson"; exit 1 }
    $MgfxcVersion = (Get-Content $toolsJson -Raw | ConvertFrom-Json).tools.'dotnet-mgcb'.version
    if (-not $MgfxcVersion) { Write-Error "No dotnet-mgcb version pinned in $toolsJson"; exit 1 }
}

$mgfxcDll = Join-Path $env:USERPROFILE ".nuget\packages\dotnet-mgcb\$MgfxcVersion\tools\net8.0\any\mgfxc.dll"
if (-not (Test-Path $mgfxcDll)) {
    Write-Error @"
mgfxc $MgfxcVersion not found at:
  $mgfxcDll
Restore the pinned tool first:  dotnet tool restore
(Do NOT substitute a newer mgfxc - 3.8.5 emits MGFX v11, not the v10 these goldens are.)
"@
    exit 1
}
# mgfxc ships as a framework-dependent dll; invoke through the dotnet host so this
# works the same way on every OS (the .exe apphost is Windows-only and, at 3.8.4.1,
# is missing a dependency in the editor package).
$mgfxc     = "dotnet"
$mgfxcArgs = @($mgfxcDll)
Write-Host "mgfxc: $mgfxcDll  (pinned $MgfxcVersion, expecting MGFX v$ExpectedMgfxVersion output)"

$shaders = Get-ChildItem $ShaderDir -Filter "*.fx" | Where-Object { $_.Extension -eq ".fx" }
Write-Host "Shaders: $($shaders.Count) files"
Write-Host ""

$results = [System.Collections.Generic.List[PSCustomObject]]::new()

foreach ($targetProfile in $Profiles) {
    $outDir = Join-Path $GoldenDir $targetProfile
    New-Item -ItemType Directory -Force $outDir | Out-Null
    Write-Host "=== Profile: $targetProfile ==="

    foreach ($shader in $shaders) {
        $outFile = Join-Path $outDir "$($shader.BaseName).mgfx"
        $output  = & $mgfxc @mgfxcArgs $shader.FullName $outFile "/Profile:$targetProfile" 2>&1
        $success = $LASTEXITCODE -eq 0

        # Guard the container version at the point of writing. A newer mgfxc emits
        # MGFX v11 and would silently rewrite the whole v10 corpus otherwise.
        if ($success -and (Test-Path $outFile)) {
            $header = [System.IO.File]::ReadAllBytes($outFile)
            if ($header.Length -lt 6 -or $header[4] -ne $ExpectedMgfxVersion) {
                $got = if ($header.Length -ge 6) { $header[4] } else { "(truncated)" }
                Write-Error @"
ORACLE MISMATCH: mgfxc $MgfxcVersion wrote MGFX v$got for $($shader.Name)/$targetProfile,
but the goldens are v$ExpectedMgfxVersion. Refusing to overwrite the corpus with a
different container version. Re-pin -MgfxcVersion, or make the version change a
deliberate, reviewed regeneration.
"@
                exit 1
            }
        }

        $results.Add([PSCustomObject]@{
            Profile = $targetProfile
            Shader  = $shader.Name
            Success = $success
            Output  = ($output -join "`n").Trim()
        })

        if ($success) {
            Write-Host "  OK   $($shader.Name)"
        } else {
            Write-Host "  FAIL $($shader.Name)"
            if ($output) { $output | ForEach-Object { Write-Host "       $_" } }
        }
    }
    Write-Host ""
}

# Summary
$ok   = ($results | Where-Object Success).Count
$fail = ($results | Where-Object { -not $_.Success }).Count
Write-Host "Done: $ok compiled, $fail failed"

if ($fail -gt 0) {
    Write-Host ""
    Write-Host "Failed shaders:"
    $results | Where-Object { -not $_.Success } | ForEach-Object {
        Write-Host "  [$($_.Profile)] $($_.Shader)"
    }
}
