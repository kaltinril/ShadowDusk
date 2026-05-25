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

.EXAMPLE
    .\tools\compile-fixtures.ps1
    .\tools\compile-fixtures.ps1 -Profiles DirectX_11
#>
param(
    [string[]] $Profiles  = @("DirectX_11", "OpenGL"),
    [string]   $ShaderDir = $null,
    [string]   $GoldenDir = $null
)

$RepoRoot  = Split-Path $PSScriptRoot -Parent
$ShaderDir = $ShaderDir ?? (Join-Path $RepoRoot "tests\fixtures\shaders")
$GoldenDir = $GoldenDir ?? (Join-Path $RepoRoot "tests\fixtures\golden")

# Locate mgfxc.exe — prefer the highest version in the NuGet cache
$mgfxc = Get-ChildItem "$env:USERPROFILE\.nuget\packages\dotnet-mgcb-editor-windows" `
    -Recurse -Filter "mgfxc.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $mgfxc) {
    Write-Error "mgfxc.exe not found. Install dotnet-mgcb-editor-windows: dotnet tool install -g dotnet-mgcb-editor-windows"
    exit 1
}
Write-Host "mgfxc: $mgfxc"

$shaders = Get-ChildItem $ShaderDir -Filter "*.fx" | Where-Object { $_.Extension -eq ".fx" }
Write-Host "Shaders: $($shaders.Count) files"
Write-Host ""

$results = [System.Collections.Generic.List[PSCustomObject]]::new()

foreach ($profile in $Profiles) {
    $outDir = Join-Path $GoldenDir $profile
    New-Item -ItemType Directory -Force $outDir | Out-Null
    Write-Host "=== Profile: $profile ==="

    foreach ($shader in $shaders) {
        $outFile = Join-Path $outDir "$($shader.BaseName).mgfx"
        $output  = & $mgfxc $shader.FullName $outFile /Profile:$profile 2>&1
        $success = $LASTEXITCODE -eq 0

        $results.Add([PSCustomObject]@{
            Profile = $profile
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
