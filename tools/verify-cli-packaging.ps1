# tools/verify-cli-packaging.ps1
#
# Phase 27 (closing Phase 9 §9.4–§9.6): repeatable verification of the CLI's three
# delivery mechanics on Windows. Scripted so "manual" never means "unrepeatable":
#
#   §9.4  dotnet pack          → the ShadowDusk.Cli tool package exists and its
#                                DotnetToolSettings.xml declares the ShadowDuskCLI command.
#   §9.5  dotnet tool install  → installed into a SCRATCH --tool-path (NEVER -g: this
#                                script must not pollute the machine), no-args shows
#                                usage on stderr with exit 1, and a real fixture compile
#                                produces output byte-identical to the normal built CLI.
#   §9.6  dotnet publish       → win-x64 self-contained single-file binary (the
#                                release.yml flag set) runs and compiles the same fixture
#                                byte-identically. (Linux/macOS RIDs → Phase 30 CI.)
#
# Usage:  .\tools\verify-cli-packaging.ps1 [-KeepScratch]
# Exits non-zero on the first failed expectation. Prints a summary table at the end.

[CmdletBinding()]
param(
    [switch] $KeepScratch
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$scratch  = Join-Path ([IO.Path]::GetTempPath()) ("shadowdusk-cli-verify-" + [Guid]::NewGuid().ToString('N'))
$cliProj  = Join-Path $repoRoot 'src/ShadowDusk.Cli/ShadowDusk.Cli.csproj'
$fixture  = Join-Path $repoRoot 'tests/fixtures/shaders/Minimal.fx'
$results  = [System.Collections.Generic.List[string]]::new()
$failed   = $false

function Note([string] $line) { $script:results.Add($line); Write-Host $line }
function Fail([string] $line) { $script:results.Add("FAIL: $line"); Write-Error $line; $script:failed = $true }

# Runs an executable, capturing exit code / stdout / stderr without throwing.
function Invoke-Cli([string] $exe, [string[]] $cliArgs) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $exe
    foreach ($a in $cliArgs) { $psi.ArgumentList.Add($a) }
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.UseShellExecute        = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    $stdout = $p.StandardOutput.ReadToEnd()
    $stderr = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    [pscustomobject]@{ ExitCode = $p.ExitCode; Stdout = $stdout; Stderr = $stderr }
}

# One CLI binary's behavioral check: no-args → usage/exit 1; fixture compile → exit 0
# + output bytes. Returns the compile output's SHA-256 (lowercase hex) or $null.
function Test-CliBinary([string] $label, [string] $exe) {
    $noArgs = Invoke-Cli $exe @()
    if ($noArgs.ExitCode -ne 1)            { Fail "$label : no-args exit code was $($noArgs.ExitCode), expected 1"; return $null }
    if ($noArgs.Stdout.Length -ne 0)       { Fail "$label : no-args wrote to stdout (must be silent there)"; return $null }
    if ($noArgs.Stderr -notmatch 'Usage:') { Fail "$label : no-args stderr lacks the usage text"; return $null }
    Note "  $label : no-args -> exit 1 + usage on stderr (the mgfxc contract)"

    $outFile = Join-Path $scratch ("out-" + [Guid]::NewGuid().ToString('N') + ".mgfx")
    $compile = Invoke-Cli $exe @($fixture, $outFile, '/Profile:OpenGL')
    if ($compile.ExitCode -ne 0) { Fail "$label : fixture compile failed (exit $($compile.ExitCode)): $($compile.Stderr)"; return $null }
    if (-not (Test-Path $outFile)) { Fail "$label : fixture compile produced no output file"; return $null }
    $hash = (Get-FileHash -Algorithm SHA256 $outFile).Hash.ToLowerInvariant()
    $size = (Get-Item $outFile).Length
    Note "  $label : Minimal.fx /Profile:OpenGL -> exit 0, $size bytes, sha256=$hash"
    return $hash
}

New-Item -ItemType Directory -Force $scratch | Out-Null
Note "Scratch: $scratch"
$version = ([xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props'))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
Note "Version under test (Directory.Build.props): $version"

try {
    # ── Baseline: the normal built CLI (what the integration tests exercise) ──────
    Note ""
    Note "== Baseline: normal build output =="
    dotnet build $cliProj -c Release --nologo -v quiet | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail 'baseline dotnet build failed'; throw 'abort' }
    $builtCli = Join-Path $repoRoot 'src/ShadowDusk.Cli/bin/Release/net8.0/ShadowDuskCLI.exe'
    if (-not (Test-Path $builtCli)) { Fail "built CLI not found at $builtCli"; throw 'abort' }
    $baselineHash = Test-CliBinary 'baseline(built)' $builtCli
    if (-not $baselineHash) { throw 'abort' }

    # ── §9.4 dotnet pack ──────────────────────────────────────────────────────────
    Note ""
    Note "== §9.4 dotnet pack =="
    $nupkgDir = Join-Path $scratch 'nupkg'
    dotnet pack $cliProj -c Release -o $nupkgDir --nologo -v quiet | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail 'dotnet pack failed'; throw 'abort' }
    $nupkg = Get-ChildItem $nupkgDir -Filter 'ShadowDusk.Cli.*.nupkg' | Where-Object Name -NotLike '*.snupkg' | Select-Object -First 1
    if (-not $nupkg) { Fail 'ShadowDusk.Cli.*.nupkg not produced'; throw 'abort' }
    Note "  package: $($nupkg.Name) ($([math]::Round($nupkg.Length / 1MB, 2)) MB)"

    $extractDir = Join-Path $scratch 'nupkg-extracted'
    Expand-Archive -Path $nupkg.FullName -DestinationPath $extractDir -Force
    $toolSettings = Get-ChildItem $extractDir -Recurse -Filter 'DotnetToolSettings.xml' | Select-Object -First 1
    if (-not $toolSettings) { Fail 'DotnetToolSettings.xml missing from the package (not a dotnet tool?)'; throw 'abort' }
    $commandName = ([xml](Get-Content $toolSettings.FullName)).DotNetCliTool.Commands.Command.Name
    if ($commandName -ne 'ShadowDuskCLI') {
        Fail "tool command name is '$commandName', expected 'ShadowDuskCLI' (NOTE: Phase 9 wrote 'mgfxc', superseded by the CLI re-brand)"
        throw 'abort'
    }
    Note "  DotnetToolSettings.xml command name: $commandName (Phase 9's 'mgfxc' expectation was superseded by the ShadowDuskCLI re-brand)"

    # ── §9.5 dotnet tool install (scratch --tool-path, NEVER -g) ──────────────────
    Note ""
    Note "== §9.5 dotnet tool install (scratch --tool-path) =="
    $toolDir = Join-Path $scratch 'tool'
    # An isolated nuget.config (clearing inherited sources AND source mappings) keeps
    # this hermetic: a machine-level packageSourceMapping otherwise rejects
    # --add-source, and the tool package is fully self-contained so the local feed is
    # all that is needed.
    $nugetConfig = Join-Path $scratch 'nuget.config'
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-verify" value="$nupkgDir" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="local-verify">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -Encoding utf8 $nugetConfig
    dotnet tool install ShadowDusk.Cli --tool-path $toolDir --configfile $nugetConfig --version $version | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail 'dotnet tool install failed'; throw 'abort' }
    $toolExe = Join-Path $toolDir 'ShadowDuskCLI.exe'
    if (-not (Test-Path $toolExe)) { Fail "installed tool shim not found at $toolExe"; throw 'abort' }
    $toolHash = Test-CliBinary 'installed-tool' $toolExe
    if (-not $toolHash) { throw 'abort' }
    if ($toolHash -ne $baselineHash) { Fail "installed tool output differs from the built CLI (tool=$toolHash baseline=$baselineHash)"; throw 'abort' }
    Note "  installed tool output is BYTE-IDENTICAL to the built CLI"

    # ── §9.6 dotnet publish win-x64 self-contained single-file ────────────────────
    Note ""
    Note "== §9.6 dotnet publish -r win-x64 --self-contained (single file) =="
    $publishDir = Join-Path $scratch 'publish-win-x64'
    dotnet restore $cliProj -r win-x64 -p:RestoreLockedMode=false --nologo -v quiet | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail 'RID restore failed'; throw 'abort' }
    dotnet publish $cliProj -r win-x64 --self-contained -c Release --no-restore -o $publishDir `
        /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true `
        --nologo -v quiet | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish failed'; throw 'abort' }
    $publishedExe = Join-Path $publishDir 'ShadowDuskCLI.exe'
    if (-not (Test-Path $publishedExe)) { Fail "published single-file binary not found at $publishedExe"; throw 'abort' }
    $exeSize = (Get-Item $publishedExe).Length
    $fileCount = (Get-ChildItem $publishDir -File).Count
    Note "  binary: ShadowDuskCLI.exe ($([math]::Round($exeSize / 1MB, 1)) MB), $fileCount file(s) in publish output"
    $pubHash = Test-CliBinary 'published(self-contained)' $publishedExe
    if (-not $pubHash) { throw 'abort' }
    if ($pubHash -ne $baselineHash) { Fail "self-contained publish output differs from the built CLI (publish=$pubHash baseline=$baselineHash)"; throw 'abort' }
    Note "  self-contained single-file output is BYTE-IDENTICAL to the built CLI"
}
catch {
    if ($_.ToString() -ne 'abort') { Fail $_.ToString() }
}
finally {
    if (-not $KeepScratch) {
        try { Remove-Item -Recurse -Force $scratch -ErrorAction Stop } catch { Write-Warning "could not clean $scratch : $_" }
    } else {
        Write-Host "Scratch kept at $scratch"
    }
}

Write-Host ""
Write-Host ("=" * 78)
if ($failed) {
    Write-Host "CLI packaging verification: FAILED (see lines above)" -ForegroundColor Red
    exit 1
}
Write-Host "CLI packaging verification: ALL PASSED (§9.4 pack, §9.5 tool-path install, §9.6 self-contained publish)" -ForegroundColor Green
exit 0
