<#
.SYNOPSIS
    Issue #289: the on-device DXC load paths of DxcLoader, on a real Android emulator.

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

    Exits non-zero if any verdict differs. Not run by `dotnet test` and not in CI (no Android
    lane): registered in docs/validation-matrix.md section 6.

.EXAMPLE
    E:\Android\SDK\emulator\emulator -avd pixel_7_-_api_34 -no-window -no-audio   # in another shell
    ./validation/AndroidGl/run-dxc-identity-checks.ps1
#>
[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 180
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$project = Join-Path $PSScriptRoot 'AndroidGl.csproj'
$pinned = Join-Path $repo 'tools\dxc\android-x64\libdxcompiler.so'
$package = 'com.shadowdusk.androidgl'
if (-not (Test-Path $pinned)) { throw "The x86_64 DXC is not restored at $pinned." }
if (-not (Get-Command adb -ErrorAction SilentlyContinue)) { throw 'adb is not on PATH.' }

$work = Join-Path ([IO.Path]::GetTempPath()) ("sd-android-dxc-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force (Join-Path $work 'foreign') | Out-Null

# The foreign build: the pinned library with one byte of its GNU build id flipped, i.e. a
# working DXC that is not ShadowDusk's build (the same construction as ForeignDxc.cs).
$bytes = [IO.File]::ReadAllBytes($pinned)
$buildId = [Convert]::FromHexString('38487f7242f477f1eefcb58e587a128c2a54906e')
$at = -1
for ($i = 0; $i -le $bytes.Length - $buildId.Length -and $at -lt 0; $i++) {
    if ($bytes[$i] -ne $buildId[0]) { continue }
    $match = $true
    for ($j = 1; $j -lt $buildId.Length -and $match; $j++) { $match = $bytes[$i + $j] -eq $buildId[$j] }
    if ($match) { $at = $i }
}
if ($at -lt 0) { throw "The pinned build id is not in $pinned; re-pin DxcNativeIdentity.AndroidX64CompilerBuildId first." }
$bytes[$at] = $bytes[$at] -bxor 0xFF
$foreign = Join-Path $work 'foreign\libdxcompiler.so'
[IO.File]::WriteAllBytes($foreign, $bytes)

function Remove-Apks {
    # The APK packaging step is incremental on its inputs and does not notice a native library
    # that was REMOVED (the 'missing' scenario kept the old one, measured), so every scenario
    # repackages from scratch.
    Get-ChildItem (Join-Path $PSScriptRoot 'bin'), (Join-Path $PSScriptRoot 'obj') -Recurse -Filter '*.apk' -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

function Invoke-Scenario([string]$Name, [string]$Dxc, [string[]]$Expect) {
    Write-Host "== $Name"
    Remove-Apks
    & dotnet build $project -c Debug "-p:AndroidGlDxcX64=$Dxc" -t:Install --nologo -v quiet | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "${Name}: build/install failed ($LASTEXITCODE)" }

    & adb shell am force-stop $package | Out-Null
    & adb logcat -c | Out-Null
    & adb shell monkey -p $package -c android.intent.category.LAUNCHER 1 2>&1 | Out-Null

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $verdict = $null
    while ((Get-Date) -lt $deadline -and -not $verdict) {
        Start-Sleep -Seconds 3
        $verdict = & adb logcat -d -s SHADOWDUSK:* |
            Where-Object { $_ -match 'ON-DEVICE COMPILE OK|COMPILE REJECTED|NATIVE MISSING' } |
            Select-Object -First 1
    }
    & adb shell am force-stop $package | Out-Null
    if (-not $verdict) { Write-Host "  FAIL  no verdict in logcat within $TimeoutSeconds s"; return $false }

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
        (Invoke-Scenario 'pinned' '' @('ON-DEVICE COMPILE OK')),
        (Invoke-Scenario 'foreign' $foreign @('COMPILE REJECTED: SD0219', "not ShadowDusk's pinned build for 'android-x64'")),
        (Invoke-Scenario 'missing' 'none' @('COMPILE REJECTED: SD0219', 'is not in this app'))
    )
}
finally {
    # Leave the device with the ordinary (pinned) build installed.
    Remove-Apks
    & dotnet build $project -c Debug -t:Install --nologo -v quiet | Out-Null
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

if ($results -contains $false) { Write-Host 'run-dxc-identity-checks: FAILED'; exit 1 }
Write-Host 'run-dxc-identity-checks: PASSED (pinned compiles; foreign and missing DXC refused with SD0219)'
