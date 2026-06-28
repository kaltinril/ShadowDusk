#Requires -Version 5.1
<#
.SYNOPSIS
    Render docs/*.puml to docfx/images/*.svg with a PINNED, SHA-256-verified PlantUML.

.DESCRIPTION
    Run before a local `dotnet docfx` build; .github/workflows/docs.yml runs it in CI so
    the published diagrams are ALWAYS regenerated from their .puml source (the .puml is the
    single source of truth).

    Supply-chain: PlantUML is downloaded from Maven Central (immutable artifacts) to
    tools/plantuml/ and VERIFIED against the pin below BEFORE it is ever executed — the same
    discipline tools/restore.ps1 applies to the native binaries. A hash mismatch is fatal (an
    unverified jar is never run). The jar is cached + gitignored.

    PlantUML uses the Smetana layout (set in the .puml via `!pragma layout smetana`), so no
    Graphviz is required — only a JRE.
#>
param([switch]$Force)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$PlantUmlVersion = '1.2024.8'
$PlantUmlSha256  = '2e1f42a9879cd25236b5725ca7db25cb9996e8e37a0a1440b2eb559f259c54aa'
$PlantUmlUrl     = "https://repo1.maven.org/maven2/net/sourceforge/plantuml/plantuml/$PlantUmlVersion/plantuml-$PlantUmlVersion.jar"

$RepoRoot = Split-Path -Parent $PSScriptRoot
# Nested 2-arg Join-Path so this runs on Windows PowerShell 5.1 too (3-arg is PS7+ only).
$JarDir   = Join-Path (Join-Path $RepoRoot 'tools') 'plantuml'
$Jar      = Join-Path $JarDir "plantuml-$PlantUmlVersion.jar"
$PumlDir  = Join-Path $RepoRoot 'docs'
$OutDir   = Join-Path (Join-Path $RepoRoot 'docfx') 'images'

New-Item -ItemType Directory -Force -Path $JarDir, $OutDir | Out-Null

function Get-Sha256([string]$Path) { (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant() }

if ((Test-Path $Jar) -and -not $Force -and (Get-Sha256 $Jar) -eq $PlantUmlSha256) {
    Write-Host "render-diagrams: PlantUML $PlantUmlVersion present, hash OK"
} else {
    Write-Host "render-diagrams: downloading PlantUML $PlantUmlVersion from Maven Central"
    $tmp = "$Jar.tmp"
    Invoke-WebRequest -Uri $PlantUmlUrl -OutFile $tmp -UseBasicParsing
    $got = Get-Sha256 $tmp
    if ($got -ne $PlantUmlSha256) {
        Remove-Item -Force $tmp
        throw "PlantUML SHA-256 mismatch (expected $PlantUmlSha256, got $got)"
    }
    Move-Item -Force $tmp $Jar
    Write-Host "render-diagrams: PlantUML $PlantUmlVersion downloaded, hash OK"
}

if (-not (Get-Command java -ErrorAction SilentlyContinue)) { throw "java not found (PlantUML needs a JRE)" }

Get-ChildItem (Join-Path $PumlDir '*.puml') | ForEach-Object {
    Write-Host "render-diagrams: $($_.Name) -> docfx/images/$($_.BaseName).svg"
    & java -jar $Jar -tsvg -o $OutDir $_.FullName
    if ($LASTEXITCODE -ne 0) { throw "PlantUML render failed for $($_.Name)" }
}
Write-Host "render-diagrams: done"
