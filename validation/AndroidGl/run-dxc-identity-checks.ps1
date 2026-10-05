<#
.SYNOPSIS
    Issues #289 and #350: the on-device load paths of DxcLoader and SpvcLoader, on a real
    Android emulator.

.DESCRIPTION
    Builds validation/AndroidGl three times, installs each APK on the connected device or
    emulator (x86_64; adb on PATH, one device attached), launches it and reads the verdict from
    logcat (tag SHADOWDUSK):

      1. pinned    the restored pinned x86_64 libdxcompiler.so      -> ON-DEVICE COMPILE OK
      2. foreign   a copy whose GNU build id differs by one byte    -> SD0219, "not ShadowDusk's pinned build"
                   (another package's DXC in the APK; issue #289's identity check, read from
                   the MAPPED image because the APK holds no separate file)
      3. missing   no x86_64 libdxcompiler.so in the APK            -> SD0219, "is not in this app"
                   (never a raw DllNotFoundException at the first P/Invoke)
      4. spvc-foreign  a libspirv-cross.so whose GNU build id differs by one byte
                                                                  -> SD0103, "not ShadowDusk's pinned build"
                   (issue #350: SpvcLoader's identity check, read from the mapped image too)
      5. spvc-missing  no x86_64 libspirv-cross.so in the APK       -> SD0103, "is not in this app"

    Scenario 1 is also the Phase 50 on-device compile + MonoGame Effect load, on the MonoGame
    Android version validation/AndroidGl pins, and it starts the app in corpus mode: every OpenGL
    entry of tests/fixtures/golden/byte-identity/manifest.json is compiled ON the device and its
    SPIR-V (DXC), GLSL (SPIRV-Cross) and .mgfx are compared with the committed desktop manifests
    (CorpusCheck.cs; the desktop half is OpenGlIntermediatesByteIdentityTests). The verdict needs
    "CORPUS RESULT: PASS"; every mismatching fixture and stage is printed.

      1b. corpus-control  the same natives, but the APK carries a manifest with ONE GLSL hash
                          changed                                  -> CORPUS RESULT: FAIL 49/50,
                                                                      1 GLSL mismatch
                   (the positive control: a corpus check that could not fail would pass anyway)

    Exits non-zero if any verdict differs. Not run by `dotnet test`; CI runs it on an API-34
    x86_64 emulator in the label-gated `Android emulator (DXC/SPIRV-Cross identity)` job of
    .github/workflows/android-emulator.yml (issue #304). Registered in
    docs/validation-matrix.md section 6. Runs on Windows, Linux and macOS (PowerShell 7).

.EXAMPLE
    E:\Android\SDK\emulator\emulator -avd pixel_7_-_api_34 -no-window -no-audio   # in another shell
    ./validation/AndroidGl/run-dxc-identity-checks.ps1
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 180
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '..'))
$project = Join-Path $PSScriptRoot 'AndroidGl.csproj'
$pinned = Join-Path $repo 'tools' 'dxc' 'android-x64' 'libdxcompiler.so'
$pinnedSpvc = Join-Path $repo 'tools' 'spirv-cross' 'android-x64' 'libspirv-cross.so'
$package = 'com.shadowdusk.androidgl'
if (-not (Test-Path $pinned)) { throw "The x86_64 DXC is not restored at $pinned (run tools/restore.*)." }
if (-not (Test-Path $pinnedSpvc)) { throw "The x86_64 SPIRV-Cross is not restored at $pinnedSpvc (run tools/restore.*)." }

# The pins the loaders check, read from the source so this script can never test another id.
function Get-Pin([string]$File, [string]$Pattern) {
    $text = Get-Content -Raw (Join-Path $repo $File)
    if ($text -notmatch $Pattern) { throw "Could not read the android-x64 build-id pin from $File." }
    return $Matches[1]
}
$dxcPin = Get-Pin (Join-Path 'src' 'ShadowDusk.HLSL' 'Dxc' 'DxcNativeIdentity.cs') 'AndroidX64CompilerBuildId = "([0-9a-f]{40})"'
$spvcPin = Get-Pin (Join-Path 'src' 'ShadowDusk.GLSL' 'Interop' 'SpvcLoader.cs') '\["android-x64"\] = "([0-9a-f]{40})"'
if (-not (Get-Command adb -ErrorAction SilentlyContinue)) { throw 'adb is not on PATH.' }

$work = Join-Path ([IO.Path]::GetTempPath()) ("sd-android-dxc-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force (Join-Path $work 'foreign') | Out-Null

# A foreign build: the pinned library with one byte of its GNU build id flipped, i.e. a working
# library that is not ShadowDusk's build (the same construction as ForeignDxc.cs).
function New-ForeignCopy([string]$Source, [string]$BuildIdHex, [string]$Destination, [string]$PinName) {
    $bytes = [IO.File]::ReadAllBytes($Source)
    $buildId = [Convert]::FromHexString($BuildIdHex)
    $at = -1
    for ($i = 0; $i -le $bytes.Length - $buildId.Length -and $at -lt 0; $i++) {
        if ($bytes[$i] -ne $buildId[0]) { continue }
        $match = $true
        for ($j = 1; $j -lt $buildId.Length -and $match; $j++) { $match = $bytes[$i + $j] -eq $buildId[$j] }
        if ($match) { $at = $i }
    }
    if ($at -lt 0) { throw "The pinned build id is not in $Source; re-pin $PinName first." }
    $bytes[$at] = $bytes[$at] -bxor 0xFF
    New-Item -ItemType Directory -Force (Split-Path $Destination) | Out-Null
    [IO.File]::WriteAllBytes($Destination, $bytes)
}

$foreign = Join-Path $work 'foreign' 'libdxcompiler.so'
New-ForeignCopy $pinned $dxcPin $foreign 'DxcNativeIdentity.AndroidX64CompilerBuildId'
$foreignSpvc = Join-Path $work 'foreign-spvc' 'libspirv-cross.so'
New-ForeignCopy $pinnedSpvc $spvcPin $foreignSpvc 'SpvcLoader.AndroidBuildIdByRid[android-x64]'

# The positive control's manifest: the committed one with the first GLSL hash's last digit changed.
$controlManifest = Join-Path $work 'control' 'intermediates-manifest.json'
New-Item -ItemType Directory -Force (Split-Path $controlManifest) | Out-Null
$manifestText = Get-Content -Raw (Join-Path $repo 'tests' 'fixtures' 'golden' 'byte-identity' 'intermediates-manifest.json')
$glslMatch = [regex]::Match($manifestText, '"glsl": "([0-9a-f]{64})"')
if (-not $glslMatch.Success) { throw 'No GLSL hash in intermediates-manifest.json.' }
$original = $glslMatch.Groups[1].Value
$changed = $original.Substring(0, 63) + $(if ($original[63] -eq '0') { '1' } else { '0' })
[IO.File]::WriteAllText($controlManifest, $manifestText.Remove($glslMatch.Groups[1].Index, 64).Insert($glslMatch.Groups[1].Index, $changed))

function Remove-Apks {
    # The APK packaging step is incremental on its inputs and does not notice a native library
    # that was REMOVED (the 'missing' scenario kept the old one, measured), so every scenario
    # repackages from scratch.
    Get-ChildItem (Join-Path $PSScriptRoot 'bin'), (Join-Path $PSScriptRoot 'obj') -Recurse -Filter '*.apk' -ErrorAction SilentlyContinue |
        Remove-Item -Force
    # The staged assets too: the asset copy skips a source that is OLDER than the staged file, so
    # the positive control's manifest (written at start-up) was silently not bundled (measured).
    Get-ChildItem (Join-Path $PSScriptRoot 'obj') -Recurse -Directory -Filter 'assets' -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force
}

function Invoke-Scenario([string]$Name, [string]$Dxc, [string[]]$Expect, [string]$Spvc = '', [switch]$Corpus,
                         [string]$CorpusExpect = 'CORPUS RESULT: PASS', [string]$Manifest = '') {
    Write-Host "== $Name"
    Remove-Apks
    & dotnet build $project -c Debug "-p:AndroidGlDxcX64=$Dxc" "-p:AndroidGlSpvcX64=$Spvc" "-p:AndroidGlIntermediatesManifest=$Manifest" -t:Install --nologo -v quiet -clp:ErrorsOnly | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "${Name}: build/install failed ($LASTEXITCODE)" }

    & adb shell am force-stop $package | Out-Null
    & adb logcat -c | Out-Null
    if ($Corpus) {
        & adb shell am start -n "$package/com.shadowdusk.androidgl.MainActivity" --es mode corpus 2>&1 | Out-Null
    }
    else {
        & adb shell monkey -p $package -c android.intent.category.LAUNCHER 1 2>&1 | Out-Null
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $verdict = $null
    $corpusVerdict = $null
    while ((Get-Date) -lt $deadline -and (-not $verdict -or ($Corpus -and -not $corpusVerdict))) {
        Start-Sleep -Seconds 3
        $log = @(& adb logcat -d -s SHADOWDUSK:*)
        $verdict = $log | Where-Object { $_ -match 'ON-DEVICE COMPILE OK|COMPILE REJECTED|NATIVE MISSING' } |
            Select-Object -First 1
        $corpusVerdict = $log | Where-Object { $_ -match 'CORPUS RESULT:' } | Select-Object -First 1
    }
    & adb shell am force-stop $package | Out-Null
    if ($Corpus) {
        & adb logcat -d -s SHADOWDUSK:* | Where-Object { $_ -match 'CORPUS (MISMATCH|COMPILE FAILED)' } | Out-Host
        if (-not $corpusVerdict) { Write-Host "  FAIL  no CORPUS RESULT in logcat within $TimeoutSeconds s"; $verdict = $null }
        elseif ($corpusVerdict -notmatch $CorpusExpect) { Write-Host "  $corpusVerdict"; Write-Host "  FAIL  expected /$CorpusExpect/"; return $false }
        else { Write-Host "  $corpusVerdict" }
    }
    if (-not $verdict) {
        Write-Host "  FAIL  no verdict in logcat within $TimeoutSeconds s; the app's log follows"
        & adb logcat -d -s SHADOWDUSK:* AndroidRuntime:* monodroid:* DOTNET:* | Select-Object -Last 80 | Out-Host
        return $false
    }

    Write-Host "  $verdict"
    $ok = $true
    foreach ($pattern in $Expect) {
        if ($verdict -notmatch $pattern) { Write-Host "  FAIL  expected /$pattern/"; $ok = $false }
    }
    if ($ok) { Write-Host '  ok' }
    return $ok
}

try {
    $results = @(
        (Invoke-Scenario 'pinned' '' @('ON-DEVICE COMPILE OK') -Corpus),
        # Positive control: the same natives against a manifest with ONE GLSL hash changed must
        # be reported, or a corpus check that cannot fail would pass every run.
        (Invoke-Scenario 'corpus-control' '' @('ON-DEVICE COMPILE OK') -Corpus -Manifest $controlManifest `
            -CorpusExpect 'CORPUS RESULT: FAIL 49/50 .*glsl mismatches 1, mgfx mismatches 0'),
        (Invoke-Scenario 'foreign' $foreign @('COMPILE REJECTED: SD0219', "not ShadowDusk's pinned build for 'android-x64'")),
        (Invoke-Scenario 'missing' 'none' @('COMPILE REJECTED: SD0219', 'is not in this app')),
        (Invoke-Scenario 'spvc-foreign' '' @('COMPILE REJECTED: SD0103', "not ShadowDusk's pinned build for 'android-x64'") $foreignSpvc),
        (Invoke-Scenario 'spvc-missing' '' @('COMPILE REJECTED: SD0103', 'is not in this app') 'none')
    )
}
finally {
    # Leave the device with the ordinary (pinned) build installed.
    Remove-Apks
    & dotnet build $project -c Debug -t:Install --nologo -v quiet | Out-Null
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

if ($results -contains $false) { Write-Host 'run-dxc-identity-checks: FAILED'; exit 1 }
Write-Host 'run-dxc-identity-checks: PASSED (pinned compiles; foreign and missing DXC refused with SD0219, foreign and missing SPIRV-Cross with SD0103)'
