# PowerShell 7+ (pwsh) required. Runs on Windows, Linux and macOS.
#Requires -Version 7.0
<#
.SYNOPSIS
    One command to take a fresh ShadowDusk clone to a verified local build.

.DESCRIPTION
    Checks prerequisites, restores everything, builds, and runs the tests — reporting
    what passed, what is missing, and the exact command to fix each gap.

    The design rule here: NOTHING IS SILENTLY SKIPPED. A step that cannot run says so
    and says why, because a setup script that quietly does nothing is worse than no
    script (the same reasoning behind the validation gates failing loudly rather than
    skipping).

    Steps, in order:
      1. Prerequisites  - .NET 8 + .NET 10 SDKs, git; optionally python/pwsh for gates.
      2. Native tools   - tools/restore.* (DXC, SPIRV-Cross, vkd3d-shader).
      3. dotnet tools   - the pinned dotnet-mgcb + docfx from .config/dotnet-tools.json.
      4. Build          - dotnet build ShadowDusk.slnx.
      5. Tests          - the FULL suite, never a filtered subset.
      6. Smoke compile  - one real .fx to .mgfx AND to .xnb through the CLI.
      7. Optional       - -WithSlang fetches the pinned Slang toolchain for experiments.
      8. Optional       - -WithRenderGates runs the Windows GPU render gates.

.PARAMETER WithSlang
    Also download the pinned Slang release into tools/slang/ and verify slangc runs.
    Slang is EXPERIMENTAL and not yet part of the product (see plan/PHASE-61). Nothing
    in the build depends on it; this is for trying .slang input by hand.

.PARAMETER WithRenderGates
    Also run validation/run-windows-render-gates.ps1. Needs Windows and a real GPU
    (DX12-capable for the DX12 gates). This is the half of the bar CI cannot produce.

.PARAMETER SkipTests
    Skip step 5. Useful when you only want a working build.

.EXAMPLE
    pwsh tools/setup-local-testing.ps1
    Prerequisites, restore, build, full test suite, smoke compile.

.EXAMPLE
    pwsh tools/setup-local-testing.ps1 -WithSlang -WithRenderGates
    Everything, including the GPU render proofs and the Slang toolchain.
#>

param(
    [switch]$WithSlang,
    [switch]$WithRenderGates,
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

# The pinned Slang release. Bump BOTH of these together, never just the version:
# the hash is the whole point of pinning (the Phase 37/40 native playbook).
$SlangVersion = '2026.14.1'
$SlangSha256  = @{
    'windows-x86_64' = '5ED0A59D650A0AF0ACA45D5DB4E083B3D8FB5CEA05748747DD95DFBE9C580658'
}

$script:Results = [System.Collections.Generic.List[object]]::new()

function Add-Result {
    param([string]$Step, [string]$State, [string]$Detail = '')
    $script:Results.Add([pscustomobject]@{ Step = $Step; State = $State; Detail = $Detail })
    $colour = switch ($State) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        default { 'Yellow' }
    }
    $line = "  [$State] $Step"
    if ($Detail) { $line += " - $Detail" }
    Write-Host $line -ForegroundColor $colour
}

function Test-Command {
    param([string]$Name)
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

Write-Host ''
Write-Host '=== ShadowDusk local testing setup ===' -ForegroundColor Cyan
Write-Host "repo: $repoRoot"
Write-Host ''

# ---------------------------------------------------------------- 1. prerequisites
Write-Host '--- 1. Prerequisites' -ForegroundColor Cyan

if (Test-Command 'dotnet') {
    $sdks = (& dotnet --list-sdks) -join "`n"
    $has8  = $sdks -match '(?m)^8\.'
    $has10 = $sdks -match '(?m)^10\.'

    if ($has8 -and $has10) {
        Add-Result '.NET SDKs 8 + 10' 'PASS'
    }
    else {
        # BOTH are needed: the shipped libraries multi-target net8.0 AND net10.0, so a
        # single-SDK box cannot build the solution at all.
        $missing = @()
        if (-not $has8)  { $missing += '8.x' }
        if (-not $has10) { $missing += '10.x' }
        Add-Result '.NET SDKs 8 + 10' 'FAIL' `
            ("missing {0}. The libraries multi-target net8.0+net10.0, so both are required. Get them from https://dotnet.microsoft.com/download" -f ($missing -join ' and '))
    }
}
else {
    Add-Result '.NET SDK' 'FAIL' 'dotnet not on PATH - install the .NET 8 and .NET 10 SDKs'
}

if (Test-Command 'git') { Add-Result 'git' 'PASS' } else { Add-Result 'git' 'WARN' 'not on PATH' }

# python drives the render-gate image comparisons only; irrelevant for build+test.
if (Test-Command 'python') {
    Add-Result 'python (render gates only)' 'PASS'
}
else {
    Add-Result 'python (render gates only)' 'WARN' 'not on PATH - only needed for -WithRenderGates'
}

if (-not $IsWindows -and $WithRenderGates) {
    Add-Result 'render gates' 'WARN' 'the Windows GPU gates only run on Windows; -WithRenderGates will be skipped'
    $WithRenderGates = $false
}

# ------------------------------------------------------------------ 2. native tools
Write-Host ''
Write-Host '--- 2. Native compiler binaries (DXC, SPIRV-Cross, vkd3d-shader)' -ForegroundColor Cyan

try {
    if ($IsWindows) { & "$repoRoot/tools/restore.ps1" | Out-Null }
    else            { & bash "$repoRoot/tools/restore.sh" | Out-Null }
    Add-Result 'tools/restore' 'PASS' 'native binaries restored'
}
catch {
    # Not fatal: the runtime natives ship transitively via NuGet. tools/ is a dev convenience.
    Add-Result 'tools/restore' 'WARN' "$($_.Exception.Message) (non-fatal: the shipped natives come from NuGet)"
}

# ------------------------------------------------------------------ 3. dotnet tools
Write-Host ''
Write-Host '--- 3. Pinned dotnet tools (mgcb, docfx)' -ForegroundColor Cyan

try {
    & dotnet tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore exited $LASTEXITCODE" }
    Add-Result 'dotnet tool restore' 'PASS' 'dotnet-mgcb available (needed by the MGCB and XNB gates)'
}
catch {
    Add-Result 'dotnet tool restore' 'FAIL' $_.Exception.Message
}

# ------------------------------------------------------------------------- 4. build
Write-Host ''
Write-Host '--- 4. Build' -ForegroundColor Cyan

& dotnet build ShadowDusk.slnx -v minimal --nologo
if ($LASTEXITCODE -eq 0) {
    Add-Result 'dotnet build ShadowDusk.slnx' 'PASS'
}
else {
    Add-Result 'dotnet build ShadowDusk.slnx' 'FAIL' "exit $LASTEXITCODE - fix this before anything below is meaningful"
    Pop-Location
    Write-Host ''
    Write-Host 'Setup stopped: the build failed.' -ForegroundColor Red
    exit 1
}

# ------------------------------------------------------------------------- 5. tests
if (-not $SkipTests) {
    Write-Host ''
    Write-Host '--- 5. Full test suite (never a filtered subset)' -ForegroundColor Cyan

    & dotnet test ShadowDusk.slnx --nologo
    if ($LASTEXITCODE -eq 0) {
        Add-Result 'dotnet test ShadowDusk.slnx' 'PASS' 'full suite green'
    }
    else {
        Add-Result 'dotnet test ShadowDusk.slnx' 'FAIL' "exit $LASTEXITCODE"
    }
}
else {
    Add-Result 'dotnet test' 'WARN' 'skipped by -SkipTests'
}

# ----------------------------------------------------------------- 6. smoke compile
Write-Host ''
Write-Host '--- 6. Smoke compile (a real .fx, both output shapes)' -ForegroundColor Cyan

$smokeDir = Join-Path ([System.IO.Path]::GetTempPath()) ("shadowdusk_smoke_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $smokeDir -Force | Out-Null
try {
    $fx = Join-Path $repoRoot 'tests/fixtures/shaders/Grayscale.fx'

    foreach ($shape in @(
        @{ Name = '.mgfx (raw effect bytes)'; Out = Join-Path $smokeDir 'Grayscale.mgfx' },
        @{ Name = '.xnb (Content.Load-ready)'; Out = Join-Path $smokeDir 'Grayscale.xnb' }
    )) {
        & dotnet run --project src/ShadowDusk.Cli -c Debug --no-build -- $fx $shape.Out /Profile:OpenGL 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0 -and (Test-Path $shape.Out)) {
            $size = (Get-Item $shape.Out).Length
            Add-Result "CLI compile $($shape.Name)" 'PASS' "$size bytes"
        }
        else {
            Add-Result "CLI compile $($shape.Name)" 'FAIL' "exit $LASTEXITCODE"
        }
    }
}
finally {
    Remove-Item $smokeDir -Recurse -Force -ErrorAction SilentlyContinue
}

# ------------------------------------------------------------------ 7. Slang (opt-in)
if ($WithSlang) {
    Write-Host ''
    Write-Host "--- 7. Slang toolchain (EXPERIMENTAL - not part of the product)" -ForegroundColor Cyan

    if (-not $IsWindows) {
        Add-Result 'Slang toolchain' 'WARN' 'this script only fetches the windows-x86_64 build so far'
    }
    else {
        $slangDir = Join-Path $repoRoot 'tools/slang'
        $slangc   = Join-Path $slangDir 'bin/slangc.exe'

        if (Test-Path $slangc) {
            Add-Result 'Slang toolchain' 'PASS' "already present at tools/slang (v$SlangVersion)"
        }
        else {
            try {
                New-Item -ItemType Directory -Path $slangDir -Force | Out-Null
                $asset = "slang-$SlangVersion-windows-x86_64.zip"
                $url   = "https://github.com/shader-slang/slang/releases/download/v$SlangVersion/$asset"
                $zip   = Join-Path $slangDir $asset

                Write-Host "  downloading $asset ..."
                Invoke-WebRequest -Uri $url -OutFile $zip

                # Verify BEFORE extracting. An unverified native is exactly what the
                # pin exists to prevent.
                $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
                $expect = $SlangSha256['windows-x86_64']
                if ($actual -ne $expect) {
                    Remove-Item $zip -Force
                    throw "SHA-256 mismatch. expected $expect, got $actual - refusing to extract"
                }

                Expand-Archive -Path $zip -DestinationPath $slangDir -Force
                Remove-Item $zip -Force

                $version = (& $slangc -v 2>&1 | Select-Object -First 1)
                Add-Result 'Slang toolchain' 'PASS' "tools/slang, slangc reports $version"
            }
            catch {
                Add-Result 'Slang toolchain' 'FAIL' $_.Exception.Message
            }
        }

        if (Test-Path $slangc) {
            Write-Host ''
            Write-Host '  Try it by hand:' -ForegroundColor DarkGray
            Write-Host '    tools/slang/bin/slangc.exe your.slang -target hlsl -entry MainPS -stage pixel -o out.hlsl' -ForegroundColor DarkGray
            Write-Host '  NOTE: slangc rejects a whole .fx file - it has no technique/pass concept,' -ForegroundColor DarkGray
            Write-Host '  so it parses the shader body but errors on the FX9 effect block (measured 2026-08-13).' -ForegroundColor DarkGray
        }
    }
}

# ----------------------------------------------------------- 8. render gates (opt-in)
if ($WithRenderGates) {
    Write-Host ''
    Write-Host '--- 8. Windows GPU render gates (the bar CI cannot produce)' -ForegroundColor Cyan

    & "$repoRoot/validation/run-windows-render-gates.ps1"
    if ($LASTEXITCODE -eq 0) {
        Add-Result 'Windows render gates' 'PASS' 'output matches the reference compiler'
    }
    else {
        Add-Result 'Windows render gates' 'FAIL' "exit $LASTEXITCODE - a render diverged from mgfxc/fxc"
    }
}

# ------------------------------------------------------------------------- summary
Write-Host ''
Write-Host '================ SUMMARY ================' -ForegroundColor Cyan
foreach ($r in $script:Results) {
    $colour = switch ($r.State) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
    Write-Host ("  [{0}] {1}" -f $r.State, $r.Step) -ForegroundColor $colour
    if ($r.Detail) { Write-Host ("         {0}" -f $r.Detail) -ForegroundColor DarkGray }
}

$failed = @($script:Results | Where-Object State -eq 'FAIL')
$warned = @($script:Results | Where-Object State -eq 'WARN')

Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host ("{0} step(s) FAILED. Each line above says what to do." -f $failed.Count) -ForegroundColor Red
}
elseif ($warned.Count -gt 0) {
    Write-Host ("Ready, with {0} optional item(s) unavailable (listed above)." -f $warned.Count) -ForegroundColor Yellow
}
else {
    Write-Host 'Ready. Everything checked out.' -ForegroundColor Green
}

if (-not $WithRenderGates) {
    Write-Host ''
    Write-Host 'NOTE: the GPU render proofs did NOT run. On a Windows box with a GPU, add' -ForegroundColor Yellow
    Write-Host '      -WithRenderGates. They are the half of the bar CI structurally cannot produce.' -ForegroundColor Yellow
}

Pop-Location
if ($failed.Count -gt 0) { exit 1 } else { exit 0 }
