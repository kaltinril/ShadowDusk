# tools/verify-contentpipeline-packaging.ps1
#
# Phase 63 (issue #203) D2: the packed ShadowDusk.ContentPipeline package consumed COLD by a
# scratch MonoGame 3.8.5 Content Builder project, locally. The same check pack-consume.yml runs
# in CI (its content-builder steps copy the same tools/contentbuilder-consumer/ files), plus the
# arm CI cannot do: the .xnb payload compared to the locally BUILT ShadowDuskCLI binary's bytes.
#
#   1. dotnet pack Core/HLSL/GLSL/Compiler/ContentPipeline at <Version>-smoke.local into a
#      scratch local feed (the smoke version exists nowhere else, so the consumer cannot
#      silently resolve the already-published nuget.org bits).
#   2. Scaffold the scratch Builder OUTSIDE the repo tree (no Directory.*.props leak), with a
#      nuget.config that maps ShadowDusk.* EXCLUSIVELY to the local feed.
#   3. dotnet run -- DesktopGL Windows: a real ContentBuilder builds Grayscale.fx through the
#      ShadowDusk pair; the program asserts the payload equals the packed compiler's bytes.
#   4. The payload is ALSO compared to src/ShadowDusk.Cli's built binary for the same profile.
#
# Usage:  .\tools\verify-contentpipeline-packaging.ps1 [-KeepScratch] [-SkipRestoreCheck]
# Exits non-zero on the first failed expectation.

[CmdletBinding()]
param(
    [switch] $KeepScratch
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$scratch  = Join-Path ([IO.Path]::GetTempPath()) ("shadowdusk-cp-verify-" + [Guid]::NewGuid().ToString('N'))
$feed     = Join-Path $scratch 'localfeed'
$consumer = Join-Path $scratch 'consumer'
$failed   = $false

function Note([string] $line) { Write-Host $line }
function Fail([string] $line) { Write-Error $line; $script:failed = $true }

function Invoke-Checked {
    param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string[]]$Arguments, [string]$WorkingDirectory = $repoRoot)
    Push-Location $WorkingDirectory
    try {
        & $Exe @Arguments
        if ($LASTEXITCODE -ne 0) { throw "'$Exe $($Arguments -join ' ')' exited with code $LASTEXITCODE" }
    } finally { Pop-Location }
}

try {
    New-Item -ItemType Directory -Force -Path $feed | Out-Null

    # --- 1. the smoke version + pack ------------------------------------------------------------
    $base = (Select-String -Path (Join-Path $repoRoot 'Directory.Build.props') -Pattern '<Version>(.*)</Version>').Matches[0].Groups[1].Value
    if (-not $base) { throw 'Could not read <Version> from Directory.Build.props' }
    $smoke = "$base-smoke.local"
    Note "Base version: $base   Smoke version: $smoke"

    Note "--- Restoring natives (idempotent) ---"
    & (Join-Path $repoRoot 'tools/restore.ps1')

    Note "--- Packing into $feed ---"
    foreach ($proj in @(
        'src/ShadowDusk.Core/ShadowDusk.Core.csproj',
        'src/ShadowDusk.HLSL/ShadowDusk.HLSL.csproj',
        'src/ShadowDusk.GLSL/ShadowDusk.GLSL.csproj',
        'src/ShadowDusk.Compiler/ShadowDusk.Compiler.csproj',
        'src/ShadowDusk.ContentPipeline/ShadowDusk.ContentPipeline.csproj')) {
        Invoke-Checked 'dotnet' @('pack', $proj, '-c', 'Release', '-o', $feed, "-p:Version=$smoke")
    }

    $pkg = Get-ChildItem $feed -Filter "ShadowDusk.ContentPipeline.$smoke.nupkg"
    if (-not $pkg) { throw "ShadowDusk.ContentPipeline.$smoke.nupkg was not produced" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($pkg.FullName)
    try {
        $names = $zip.Entries | ForEach-Object { $_.FullName }
        if ($names -notcontains 'lib/net8.0/ShadowDusk.ContentPipeline.dll') { Fail 'the package has no lib/net8.0/ShadowDusk.ContentPipeline.dll' }
        $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -eq 'ShadowDusk.ContentPipeline.nuspec' }
        $reader = New-Object IO.StreamReader($nuspecEntry.Open())
        try { $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($nuspec -notmatch 'id="MonoGame\.Framework\.Content\.Pipeline"') { Fail 'the nuspec does not declare MonoGame.Framework.Content.Pipeline' }
        if ($nuspec -notmatch 'id="ShadowDusk\.Compiler"')                  { Fail 'the nuspec does not declare ShadowDusk.Compiler' }
        Note "  package: lib/net8.0 present, nuspec declares MonoGame.Framework.Content.Pipeline + ShadowDusk.Compiler"
    } finally { $zip.Dispose() }
    if ($failed) { throw 'package shape check failed' }

    # --- 2. the scratch Builder --------------------------------------------------------------------
    Note "--- Scaffolding the scratch Content Builder in $consumer ---"
    New-Item -ItemType Directory -Force -Path (Join-Path $consumer 'Assets/Effects') | Out-Null
    Copy-Item (Join-Path $repoRoot 'tools/contentbuilder-consumer/Program.cs') $consumer
    (Get-Content (Join-Path $repoRoot 'tools/contentbuilder-consumer/ScratchContentBuilder.csproj') -Raw).Replace('SMOKE_VERSION', $smoke) |
        Set-Content (Join-Path $consumer 'ScratchContentBuilder.csproj') -NoNewline
    Copy-Item (Join-Path $repoRoot 'tests/fixtures/shaders/Grayscale.fx') (Join-Path $consumer 'Assets/Effects')
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="localfeed" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="localfeed"><package pattern="ShadowDusk.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content (Join-Path $consumer 'nuget.config')

    # --- 3. the consumer run: real ContentBuilder, packed pair, payload == packed compiler -----
    Note "--- Building + running the scratch Builder (DesktopGL, Windows) ---"
    Invoke-Checked 'dotnet' @('run', '-c', 'Release', '--', 'DesktopGL', 'Windows') $consumer

    # --- 4. payload == the locally BUILT CLI's bytes (the arm CI does not have) --------------
    Note "--- Comparing the .xnb payloads to src/ShadowDusk.Cli's built binary ---"
    Invoke-Checked 'dotnet' @('build', 'src/ShadowDusk.Cli/ShadowDusk.Cli.csproj', '-c', 'Release')
    $cli = Get-ChildItem (Join-Path $repoRoot 'src/ShadowDusk.Cli/bin') -Recurse -Filter 'ShadowDuskCLI.exe' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $cli) { throw 'ShadowDuskCLI.exe not found under src/ShadowDusk.Cli/bin' }

    function Read-XnbPayload([byte[]] $b) {
        $i = 10
        $read7 = {
            $r = 0; $s = 0
            while ($true) { $x = $b[$script:i]; $script:i++; $r = $r -bor (($x -band 0x7F) -shl $s); if (($x -band 0x80) -eq 0) { return $r }; $s += 7 }
        }
        $script:i = $i
        $readers = & $read7
        for ($k = 0; $k -lt $readers; $k++) { $len = & $read7; $script:i += $len + 4 }
        & $read7 | Out-Null; & $read7 | Out-Null
        $len = [BitConverter]::ToInt32($b, $script:i); $script:i += 4
        return $b[$script:i..($script:i + $len - 1)]
    }

    foreach ($case in @(@{ Platform = 'DesktopGL'; Profile = 'OpenGL' }, @{ Platform = 'Windows'; Profile = 'DirectX_11' })) {
        $xnb = Get-ChildItem (Join-Path $consumer "bin/Release/out-$($case.Platform)") -Recurse -Filter 'Grayscale.xnb' | Select-Object -First 1
        if (-not $xnb) { Fail "no Grayscale.xnb produced for $($case.Platform)"; continue }
        $payload = Read-XnbPayload ([IO.File]::ReadAllBytes($xnb.FullName))
        $cliOut = Join-Path $scratch "cli-$($case.Profile).mgfx"
        Invoke-Checked $cli.FullName @((Join-Path $consumer 'bin/Release/Assets/Effects/Grayscale.fx'), $cliOut, "/Profile:$($case.Profile)")
        $cliBytes = [IO.File]::ReadAllBytes($cliOut)
        if ([Linq.Enumerable]::SequenceEqual([byte[]]$payload, [byte[]]$cliBytes)) {
            Note "  $($case.Platform): .xnb payload ($($payload.Length) B) == ShadowDuskCLI /Profile:$($case.Profile)"
        } else {
            Fail "  $($case.Platform): .xnb payload ($($payload.Length) B) != ShadowDuskCLI /Profile:$($case.Profile) ($($cliBytes.Length) B)"
        }
    }

    if ($failed) { throw 'one or more expectations failed' }
    Note "`nContentPipeline packaging: OK (packed cold, real ContentBuilder, payload == packed compiler == built CLI)"
}
finally {
    if ($KeepScratch) { Note "scratch kept at $scratch" }
    else { try { Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue } catch { } }
}
