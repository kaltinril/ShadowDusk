# Phase 10 — Cross-Platform CI (GitHub Actions)

## Overview

This phase wires up GitHub Actions CI that builds, tests, and packages ShadowDusk across Linux, macOS, and Windows. It is the final phase of the core pipeline and validates every prior phase end-to-end in a clean, reproducible environment.

**Inputs:** A complete, passing local build from Phases 1–9.
**Outputs:** Green CI badges on every push/PR; self-contained binaries and a NuGet package published on every `v*.*.*` tag.

---

## Scope and Non-Goals

**In scope:**
- `ci.yml` — build + unit test matrix on all 3 OS (push/PR)
- `release.yml` — build + test + self-contained publish + NuGet push on `v*.*.*` tags
- Native binary restore and caching for SPIRV-Cross
- `chmod +x` fix for native shared libraries on Linux/macOS
- RID matrix covering all 4 targets
- Hash verification of all downloaded native binaries

**Out of scope:**
- macOS code signing / notarization (future work)
- Windows code signing (future work)
- Docker-based Linux builds (GitHub-hosted runners are sufficient)
- ARM Linux runners (not available on GitHub free tier; add later)
- Release notes generation / changelog (separate tooling)

---

## 1. RID Matrix

| RID | Hosted Runner OS | Native SPIRV-Cross Artifact | Notes |
|---|---|---|---|
| `win-x64` | `windows-latest` | `spirv-cross-win-x64.zip` | Primary dev platform; DXC via Vortice.Dxc NuGet |
| `linux-x64` | `ubuntu-latest` | `spirv-cross-linux-x64.tar.gz` | CI + server deployments; `.so` needs `chmod +x` |
| `osx-x64` | `macos-latest` | `spirv-cross-osx-x64.tar.gz` | Intel Mac legacy; `.dylib` needs `chmod +x` |
| `osx-arm64` | `macos-latest` | `spirv-cross-osx-arm64.tar.gz` | M1/M2/M3; cross-publish from Intel runner |

> **Note:** `osx-arm64` self-contained publish runs on the macOS Intel runner with `-r osx-arm64`. GitHub's `macos-latest` is Apple Silicon as of late 2024; if the runner architecture changes, adjust the `macos-latest` annotation but the RID flag stays the same.

---

## 2. Repository Secrets Required

| Secret | Used in | Purpose |
|---|---|---|
| `NUGET_API_KEY` | `release.yml` | Push to `api.nuget.org` |

Configure via **Settings → Secrets and variables → Actions → New repository secret**.

---

## 3. Native Binary Versioning

All native binary versions and hashes are pinned in a single file:

```
tools/native-versions.json
```

### 3.1 `tools/native-versions.json` format

```json
{
  "spirv-cross": {
    "version": "vulkan-sdk-1.3.280.0",
    "artifacts": {
      "win-x64":    { "url": "https://github.com/KhronosGroup/SPIRV-Cross/releases/download/vulkan-sdk-1.3.280.0/spirv-cross-win-x64.zip",      "sha256": "<sha256>" },
      "linux-x64":  { "url": "https://github.com/KhronosGroup/SPIRV-Cross/releases/download/vulkan-sdk-1.3.280.0/spirv-cross-linux-x64.tar.gz",  "sha256": "<sha256>" },
      "osx-x64":    { "url": "https://github.com/KhronosGroup/SPIRV-Cross/releases/download/vulkan-sdk-1.3.280.0/spirv-cross-osx-x64.tar.gz",    "sha256": "<sha256>" },
      "osx-arm64":  { "url": "https://github.com/KhronosGroup/SPIRV-Cross/releases/download/vulkan-sdk-1.3.280.0/spirv-cross-osx-arm64.tar.gz",  "sha256": "<sha256>" }
    }
  }
}
```

Replace `<sha256>` placeholders with the actual SHA-256 digest of each archive. Compute with:

```bash
# Linux / macOS
sha256sum spirv-cross-linux-x64.tar.gz

# Windows PowerShell
Get-FileHash spirv-cross-win-x64.zip -Algorithm SHA256
```

> **Vortice.Dxc handles DXC.** DXC native binaries (`dxcompiler.dll`, `libdxcompiler.so`, `libdxcompiler.dylib`) are bundled inside the `Vortice.Dxc` NuGet package and resolved automatically via MSBuild. Do **not** add DXC to `native-versions.json`.

### 3.2 Restore scripts

Two scripts implement native binary restore. Both read `tools/native-versions.json`, download the platform-appropriate artifact, **verify the SHA-256 hash**, and extract to `tools/spirv-cross/`.

**`tools/restore.sh`** (Linux / macOS)

```bash
#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSIONS_FILE="$SCRIPT_DIR/native-versions.json"
OUT_DIR="$SCRIPT_DIR/spirv-cross"

# Detect RID
RID="${SPIRV_CROSS_RID:-}"
if [[ -z "$RID" ]]; then
  OS="$(uname -s)"
  ARCH="$(uname -m)"
  if [[ "$OS" == "Darwin" && "$ARCH" == "arm64" ]]; then
    RID="osx-arm64"
  elif [[ "$OS" == "Darwin" ]]; then
    RID="osx-x64"
  elif [[ "$OS" == "Linux" ]]; then
    RID="linux-x64"
  else
    echo "ERROR: Unsupported OS: $OS" >&2
    exit 1
  fi
fi

URL="$(jq -r ".\"spirv-cross\".artifacts.\"$RID\".url" "$VERSIONS_FILE")"
EXPECTED_HASH="$(jq -r ".\"spirv-cross\".artifacts.\"$RID\".sha256" "$VERSIONS_FILE")"
ARCHIVE="$(basename "$URL")"
TMP="$(mktemp -d)"

echo "Downloading SPIRV-Cross ($RID)..."
curl -fsSL "$URL" -o "$TMP/$ARCHIVE"

echo "Verifying SHA-256..."
if command -v sha256sum &>/dev/null; then
  ACTUAL_HASH="$(sha256sum "$TMP/$ARCHIVE" | awk '{print $1}')"
else
  ACTUAL_HASH="$(shasum -a 256 "$TMP/$ARCHIVE" | awk '{print $1}')"
fi
if [[ "$ACTUAL_HASH" != "$EXPECTED_HASH" ]]; then
  echo "ERROR: Hash mismatch for $ARCHIVE" >&2
  echo "  Expected: $EXPECTED_HASH" >&2
  echo "  Got:      $ACTUAL_HASH" >&2
  rm -rf "$TMP"
  exit 1
fi

mkdir -p "$OUT_DIR"
tar -xzf "$TMP/$ARCHIVE" -C "$OUT_DIR" --strip-components=1
chmod +x "$OUT_DIR"/libspirv-cross-c-shared.* 2>/dev/null || true
rm -rf "$TMP"

echo "SPIRV-Cross restored to $OUT_DIR"
```

**`tools/restore.ps1`** (Windows)

```powershell
#Requires -Version 7
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ScriptDir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$VersionsFile = Join-Path $ScriptDir 'native-versions.json'
$OutDir       = Join-Path $ScriptDir 'spirv-cross'

$versions = Get-Content $VersionsFile | ConvertFrom-Json
$artifact  = $versions.'spirv-cross'.artifacts.'win-x64'
$Url       = $artifact.url
$Expected  = $artifact.sha256.ToLowerInvariant()
$Archive   = [System.IO.Path]::GetFileName($Url)
$Tmp       = [System.IO.Path]::GetTempPath() | Join-Path -ChildPath ([System.IO.Path]::GetRandomFileName())

New-Item -ItemType Directory -Force -Path $Tmp | Out-Null

Write-Host "Downloading SPIRV-Cross (win-x64)..."
Invoke-WebRequest -Uri $Url -OutFile (Join-Path $Tmp $Archive) -UseBasicParsing

Write-Host "Verifying SHA-256..."
$Actual = (Get-FileHash (Join-Path $Tmp $Archive) -Algorithm SHA256).Hash.ToLowerInvariant()
if ($Actual -ne $Expected) {
    Write-Error "Hash mismatch for $Archive`n  Expected: $Expected`n  Got:      $Actual"
    Remove-Item $Tmp -Recurse -Force
    exit 1
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Expand-Archive -Path (Join-Path $Tmp $Archive) -DestinationPath $OutDir -Force
Remove-Item $Tmp -Recurse -Force

Write-Host "SPIRV-Cross restored to $OutDir"
```

---

## 4. Native Binary Bundling in `ShadowDusk.GLSL`

SPIRV-Cross shared libraries must be included in the NuGet package and self-contained publish under the standard `runtimes/<rid>/native/` convention. MSBuild and the .NET runtime locate them automatically via `NativeLibrary.Load()`.

### 4.1 Project file additions (`ShadowDusk.GLSL.csproj`)

```xml
<!-- Teach MSBuild to copy the correct RID-native binary to the output directory. -->
<!-- Files live under tools/spirv-cross/ after running tools/restore.sh|ps1. -->
<ItemGroup>
  <!-- Windows -->
  <Content Include="$(RepoRoot)tools\spirv-cross\spirv-cross-c-shared.dll"
           Condition="'$(RuntimeIdentifier)' == 'win-x64' Or '$(OS)' == 'Windows_NT'">
    <Link>runtimes\win-x64\native\spirv-cross-c-shared.dll</Link>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <Pack>true</Pack>
    <PackagePath>runtimes/win-x64/native/</PackagePath>
  </Content>

  <!-- Linux -->
  <Content Include="$(RepoRoot)tools\spirv-cross\libspirv-cross-c-shared.so"
           Condition="'$(RuntimeIdentifier)' == 'linux-x64'">
    <Link>runtimes\linux-x64\native\libspirv-cross-c-shared.so</Link>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <Pack>true</Pack>
    <PackagePath>runtimes/linux-x64/native/</PackagePath>
  </Content>

  <!-- macOS Intel -->
  <Content Include="$(RepoRoot)tools\spirv-cross\libspirv-cross-c-shared.dylib"
           Condition="'$(RuntimeIdentifier)' == 'osx-x64'">
    <Link>runtimes\osx-x64\native\libspirv-cross-c-shared.dylib</Link>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <Pack>true</Pack>
    <PackagePath>runtimes/osx-x64/native/</PackagePath>
  </Content>

  <!-- macOS Apple Silicon -->
  <Content Include="$(RepoRoot)tools\spirv-cross\libspirv-cross-c-shared.dylib"
           Condition="'$(RuntimeIdentifier)' == 'osx-arm64'">
    <Link>runtimes\osx-arm64\native\libspirv-cross-c-shared.dylib</Link>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <Pack>true</Pack>
    <PackagePath>runtimes/osx-arm64/native/</PackagePath>
  </Content>
</ItemGroup>
```

> Set `$(RepoRoot)` via `Directory.Build.props`:
> ```xml
> <PropertyGroup>
>   <RepoRoot>$([MSBuild]::NormalizeDirectory($([MSBuild]::GetDirectoryNameOfFileAbove($(MSBuildProjectDirectory), 'ShadowDusk.sln'))))</RepoRoot>
> </PropertyGroup>
> ```

### 4.2 `NativeLibrary.Load()` call site (`SpirvCrossNativeLoader.cs`)

```csharp
// src/ShadowDusk.GLSL/SpirvCrossNativeLoader.cs
#nullable enable
using System.Reflection;
using System.Runtime.InteropServices;

namespace ShadowDusk.GLSL;

internal static class SpirvCrossNativeLoader
{
    private static readonly string LibraryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "spirv-cross-c-shared.dll"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? "libspirv-cross-c-shared.dylib"
            : "libspirv-cross-c-shared.so";

    internal static nint Load()
    {
        // Search order:
        // 1. runtimes/<rid>/native/ relative to the assembly (self-contained publish)
        // 2. Directory of the executing assembly (development / test)
        // 3. System library path (fallback for Linux package manager installs)
        return NativeLibrary.Load(
            LibraryName,
            Assembly.GetExecutingAssembly(),
            DllImportSearchPath.AssemblyDirectory |
            DllImportSearchPath.ApplicationDirectory |
            DllImportSearchPath.SafeDirectories);
    }
}
```

---

## 5. Workflow Files Layout

```
.github/
└── workflows/
    ├── ci.yml        # push / PR → build + unit tests on all 3 OS
    └── release.yml   # v*.*.* tag → build + test + publish binaries + NuGet
```

---

## 6. `ci.yml` — Continuous Integration Workflow

```yaml
# .github/workflows/ci.yml
name: CI

on:
  push:
    branches: [ main ]
  pull_request:
    branches: [ main ]

concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true

env:
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1'
  DOTNET_NOLOGO: '1'
  DOTNET_CLI_TELEMETRY_OPTOUT: '1'

jobs:
  build-and-test:
    name: Build & Test (${{ matrix.os }})
    runs-on: ${{ matrix.os }}
    strategy:
      fail-fast: false
      matrix:
        os: [ ubuntu-latest, macos-latest, windows-latest ]

    steps:
      # ── 1. Checkout ──────────────────────────────────────────────────────────
      - name: Checkout
        uses: actions/checkout@v4

      # ── 2. .NET SDK ───────────────────────────────────────────────────────────
      - name: Setup .NET 8
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      # ── 3. Cache NuGet packages ───────────────────────────────────────────────
      - name: Cache NuGet
        uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('**/packages.lock.json', '**/Directory.Packages.props') }}
          restore-keys: |
            nuget-${{ runner.os }}-

      # ── 4. Cache native binaries ──────────────────────────────────────────────
      - name: Cache native tools
        uses: actions/cache@v4
        id: cache-native
        with:
          path: tools/spirv-cross
          key: native-${{ runner.os }}-${{ hashFiles('tools/native-versions.json') }}

      # ── 5. Restore native binaries (skip if cache hit) ────────────────────────
      - name: Restore native tools (Linux / macOS)
        if: steps.cache-native.outputs.cache-hit != 'true' && runner.os != 'Windows'
        run: |
          chmod +x tools/restore.sh
          ./tools/restore.sh

      - name: Restore native tools (Windows)
        if: steps.cache-native.outputs.cache-hit != 'true' && runner.os == 'Windows'
        shell: pwsh
        run: .\tools\restore.ps1

      # ── 6. Fix file permissions on native libraries (Linux / macOS) ───────────
      # CRITICAL: git checkout does NOT preserve +x on .so / .dylib files.
      # This step re-applies execute permission after every checkout, even on
      # cache hits, because the cache restores file content but not permissions.
      - name: Fix native library permissions (Linux / macOS)
        if: runner.os != 'Windows'
        run: |
          find tools/spirv-cross \( -name "*.so" -o -name "*.dylib" \) -exec chmod +x {} + 2>/dev/null || true

      # ── 7. Restore NuGet packages ─────────────────────────────────────────────
      - name: dotnet restore
        run: dotnet restore --locked-mode

      # ── 8. Build ──────────────────────────────────────────────────────────────
      - name: dotnet build
        run: dotnet build --no-restore -c Release

      # ── 9. Unit tests ─────────────────────────────────────────────────────────
      - name: dotnet test (unit)
        run: >
          dotnet test --no-build -c Release
          --filter "Category!=Integration"
          --logger "trx;LogFileName=unit-${{ matrix.os }}.trx"
          --results-directory TestResults/

      # ── 10. Upload test results ───────────────────────────────────────────────
      - name: Upload test results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: test-results-${{ matrix.os }}
          path: TestResults/*.trx
          retention-days: 7

  # Optional slow path: integration tests. Gate on the 'run-integration' label
  # on a PR, or run automatically on pushes to main.
  integration-tests:
    name: Integration Tests (${{ matrix.os }})
    runs-on: ${{ matrix.os }}
    needs: build-and-test
    if: |
      github.event_name == 'push' ||
      contains(github.event.pull_request.labels.*.name, 'run-integration')
    strategy:
      fail-fast: false
      matrix:
        os: [ ubuntu-latest, macos-latest, windows-latest ]

    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup .NET 8
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Cache NuGet
        uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('**/packages.lock.json', '**/Directory.Packages.props') }}
          restore-keys: |
            nuget-${{ runner.os }}-

      - name: Cache native tools
        uses: actions/cache@v4
        id: cache-native
        with:
          path: tools/spirv-cross
          key: native-${{ runner.os }}-${{ hashFiles('tools/native-versions.json') }}

      - name: Restore native tools (Linux / macOS)
        if: steps.cache-native.outputs.cache-hit != 'true' && runner.os != 'Windows'
        run: |
          chmod +x tools/restore.sh
          ./tools/restore.sh

      - name: Restore native tools (Windows)
        if: steps.cache-native.outputs.cache-hit != 'true' && runner.os == 'Windows'
        shell: pwsh
        run: .\tools\restore.ps1

      - name: Fix native library permissions (Linux / macOS)
        if: runner.os != 'Windows'
        run: |
          find tools/spirv-cross \( -name "*.so" -o -name "*.dylib" \) -exec chmod +x {} + 2>/dev/null || true

      - name: dotnet restore
        run: dotnet restore --locked-mode

      - name: dotnet build
        run: dotnet build --no-restore -c Release

      - name: dotnet test (integration)
        run: >
          dotnet test --no-build -c Release
          --filter "Category=Integration"
          --logger "trx;LogFileName=integration-${{ matrix.os }}.trx"
          --results-directory TestResults/
          -- RunConfiguration.TestSessionTimeout=300000

      - name: Upload test results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: integration-results-${{ matrix.os }}
          path: TestResults/*.trx
          retention-days: 14
```

---

## 7. `release.yml` — Release Workflow

```yaml
# .github/workflows/release.yml
name: Release

on:
  push:
    tags:
      - 'v*.*.*'

env:
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1'
  DOTNET_NOLOGO: '1'
  DOTNET_CLI_TELEMETRY_OPTOUT: '1'

jobs:
  # ── Build + test on all 3 OS before releasing ──────────────────────────────
  build-and-test:
    name: Build & Test (${{ matrix.os }})
    runs-on: ${{ matrix.os }}
    strategy:
      fail-fast: true
      matrix:
        os: [ ubuntu-latest, macos-latest, windows-latest ]

    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup .NET 8
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Cache NuGet
        uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('**/packages.lock.json', '**/Directory.Packages.props') }}
          restore-keys: |
            nuget-${{ runner.os }}-

      - name: Cache native tools
        uses: actions/cache@v4
        id: cache-native
        with:
          path: tools/spirv-cross
          key: native-${{ runner.os }}-${{ hashFiles('tools/native-versions.json') }}

      - name: Restore native tools (Linux / macOS)
        if: steps.cache-native.outputs.cache-hit != 'true' && runner.os != 'Windows'
        run: |
          chmod +x tools/restore.sh
          ./tools/restore.sh

      - name: Restore native tools (Windows)
        if: steps.cache-native.outputs.cache-hit != 'true' && runner.os == 'Windows'
        shell: pwsh
        run: .\tools\restore.ps1

      - name: Fix native library permissions (Linux / macOS)
        if: runner.os != 'Windows'
        run: |
          find tools/spirv-cross \( -name "*.so" -o -name "*.dylib" \) -exec chmod +x {} + 2>/dev/null || true

      - name: dotnet restore
        run: dotnet restore --locked-mode

      - name: dotnet build
        run: dotnet build --no-restore -c Release

      - name: dotnet test (unit + integration)
        run: >
          dotnet test --no-build -c Release
          --logger "trx;LogFileName=release-test-${{ matrix.os }}.trx"
          --results-directory TestResults/

      - name: Upload test results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: release-test-results-${{ matrix.os }}
          path: TestResults/*.trx
          retention-days: 30

  # ── Self-contained publish per RID ─────────────────────────────────────────
  publish:
    name: Publish (${{ matrix.rid }})
    runs-on: ${{ matrix.os }}
    needs: build-and-test
    strategy:
      fail-fast: false
      matrix:
        include:
          - rid: win-x64
            os: windows-latest
          - rid: linux-x64
            os: ubuntu-latest
          - rid: osx-x64
            os: macos-latest
          - rid: osx-arm64
            os: macos-latest   # cross-publish osx-arm64 from the macOS runner

    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup .NET 8
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Cache NuGet
        uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('**/packages.lock.json', '**/Directory.Packages.props') }}
          restore-keys: |
            nuget-${{ runner.os }}-

      - name: Cache native tools
        uses: actions/cache@v4
        id: cache-native
        with:
          path: tools/spirv-cross
          key: native-${{ runner.os }}-${{ hashFiles('tools/native-versions.json') }}

      - name: Restore native tools (Linux / macOS)
        if: steps.cache-native.outputs.cache-hit != 'true' && runner.os != 'Windows'
        env:
          SPIRV_CROSS_RID: ${{ matrix.rid == 'osx-x64' && 'osx-x64' || '' }}
        run: |
          chmod +x tools/restore.sh
          ./tools/restore.sh

      - name: Restore native tools (Windows)
        if: steps.cache-native.outputs.cache-hit != 'true' && runner.os == 'Windows'
        shell: pwsh
        run: .\tools\restore.ps1

      - name: Fix native library permissions (Linux / macOS)
        if: runner.os != 'Windows'
        run: |
          find tools/spirv-cross \( -name "*.so" -o -name "*.dylib" \) -exec chmod +x {} + 2>/dev/null || true

      - name: dotnet restore
        run: dotnet restore --locked-mode

      - name: Publish self-contained (${{ matrix.rid }})
        run: >
          dotnet publish src/ShadowDusk.Cli/ShadowDusk.Cli.csproj
          -r ${{ matrix.rid }}
          --self-contained
          -c Release
          -o publish/${{ matrix.rid }}
          /p:PublishSingleFile=true
          /p:IncludeNativeLibrariesForSelfExtract=true
          /p:EnableCompressionInSingleFile=true

      # Verify the output binary is present and named correctly
      - name: Verify binary exists (Linux / macOS)
        if: runner.os != 'Windows'
        run: |
          test -f publish/${{ matrix.rid }}/mgfxc || \
            (echo "ERROR: mgfxc binary not found in publish/${{ matrix.rid }}/" && exit 1)

      - name: Verify binary exists (Windows)
        if: runner.os == 'Windows'
        shell: pwsh
        run: |
          if (-not (Test-Path "publish\${{ matrix.rid }}\mgfxc.exe")) {
            Write-Error "mgfxc.exe not found in publish\${{ matrix.rid }}\"
            exit 1
          }

      # Create a zip archive for the release asset
      - name: Archive publish output (Linux / macOS)
        if: runner.os != 'Windows'
        run: |
          cd publish
          tar -czf ../shadowdusk-${{ matrix.rid }}.tar.gz ${{ matrix.rid }}/

      - name: Archive publish output (Windows)
        if: runner.os == 'Windows'
        shell: pwsh
        run: |
          Compress-Archive -Path "publish\${{ matrix.rid }}\*" `
            -DestinationPath "shadowdusk-${{ matrix.rid }}.zip"

      - name: Upload publish artifact
        uses: actions/upload-artifact@v4
        with:
          name: shadowdusk-${{ matrix.rid }}
          path: |
            shadowdusk-${{ matrix.rid }}.tar.gz
            shadowdusk-${{ matrix.rid }}.zip
          if-no-files-found: error
          retention-days: 1

  # ── NuGet pack + push ──────────────────────────────────────────────────────
  nuget:
    name: NuGet Pack & Push
    runs-on: ubuntu-latest
    needs: build-and-test

    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup .NET 8
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Cache NuGet
        uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('**/packages.lock.json', '**/Directory.Packages.props') }}
          restore-keys: |
            nuget-${{ runner.os }}-

      - name: Cache native tools
        uses: actions/cache@v4
        id: cache-native
        with:
          path: tools/spirv-cross
          key: native-${{ runner.os }}-${{ hashFiles('tools/native-versions.json') }}

      - name: Restore native tools
        if: steps.cache-native.outputs.cache-hit != 'true'
        run: |
          chmod +x tools/restore.sh
          ./tools/restore.sh

      - name: Fix native library permissions
        run: |
          find tools/spirv-cross \( -name "*.so" -o -name "*.dylib" \) -exec chmod +x {} + 2>/dev/null || true

      - name: dotnet restore
        run: dotnet restore --locked-mode

      # Extract version from the tag (strips leading 'v')
      - name: Set package version
        id: version
        run: echo "VERSION=${GITHUB_REF_NAME#v}" >> "$GITHUB_OUTPUT"

      - name: dotnet pack
        run: >
          dotnet pack src/ShadowDusk.Cli/ShadowDusk.Cli.csproj
          -c Release
          -o nupkg
          /p:PackageVersion=${{ steps.version.outputs.VERSION }}
          /p:Version=${{ steps.version.outputs.VERSION }}

      - name: Verify .nupkg was produced
        run: |
          ls -la nupkg/
          test -n "$(find nupkg -name '*.nupkg' -maxdepth 1)" || \
            (echo "ERROR: No .nupkg found in nupkg/" && exit 1)

      - name: Push to NuGet.org
        run: >
          dotnet nuget push nupkg/*.nupkg
          --api-key ${{ secrets.NUGET_API_KEY }}
          --source https://api.nuget.org/v3/index.json
          --skip-duplicate

      - name: Upload NuGet artifact
        uses: actions/upload-artifact@v4
        with:
          name: nupkg
          path: nupkg/*.nupkg
          retention-days: 30

  # ── Create GitHub Release with all assets ─────────────────────────────────
  github-release:
    name: GitHub Release
    runs-on: ubuntu-latest
    needs: [ publish, nuget ]
    permissions:
      contents: write

    steps:
      - name: Download all publish artifacts
        uses: actions/download-artifact@v4
        with:
          pattern: shadowdusk-*
          merge-multiple: true
          path: release-assets/

      - name: Download NuGet artifact
        uses: actions/download-artifact@v4
        with:
          name: nupkg
          path: release-assets/

      - name: Create GitHub Release
        uses: softprops/action-gh-release@v2
        with:
          files: release-assets/*
          generate_release_notes: true
          fail_on_unmatched_files: true
```

---

## 8. Unix File Permissions — Critical Detail

This is the most common CI pitfall when bundling native shared libraries.

### The problem

Git does not store the execute bit for non-script files on Windows. When GitHub Actions checks out the repository on Linux or macOS:

1. The `tools/restore.sh` script itself may lack `+x` — always run `chmod +x tools/restore.sh` before invoking it.
2. The native `.so`/`.dylib` files extracted from the archive cache may lack `+x` because the cache action restores file content but not permissions.
3. `NativeLibrary.Load()` on Linux requires the `.so` to have execute permission set, or it will throw `DllNotFoundException` with a misleading "not found" message even though the file exists.

### The fix

Apply `chmod +x` in two places in every Linux/macOS job:

1. **After native tool restore** (in `restore.sh` itself — already included in Section 3.2).
2. **After every cache restore** (in the workflow step "Fix native library permissions") — this step runs unconditionally, even on cache hits:

```yaml
- name: Fix native library permissions (Linux / macOS)
  if: runner.os != 'Windows'
  run: |
    find tools/spirv-cross \( -name "*.so" -o -name "*.dylib" \) -exec chmod +x {} + 2>/dev/null || true
```

The `-exec chmod +x {} +` form with `2>/dev/null || true` is used instead of `xargs -r` because `-r`/`--no-run-if-empty` is a GNU extension that does not exist on macOS's BSD `xargs`. The `|| true` prevents an error exit if no files match (e.g., before the first restore).

### Why `chmod +x` even for `.so` files?

On Linux, `dlopen()` (which `NativeLibrary.Load()` calls) does check execute permissions on shared objects when the file is being loaded from a path that is not a standard system library directory. A file in a project-local path like `runtimes/linux-x64/native/` is treated as a regular file load, not a system library load — so the execute bit matters.

---

## 9. Locking NuGet Packages

The CI step `dotnet restore --locked-mode` requires package lock files to be committed. Generate them once:

```bash
dotnet restore --use-lock-file
git add **/packages.lock.json
git commit -m "chore: add NuGet package lock files"
```

Add this property to `Directory.Build.props` to make lock file generation the default:

```xml
<PropertyGroup>
  <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  <!-- Prevent lock file updates during CI restore -->
  <RestoreLockedMode Condition="'$(CI)' == 'true'">true</RestoreLockedMode>
</PropertyGroup>
```

> **Note:** GitHub Actions sets `CI=true` automatically, so `RestoreLockedMode` activates only in CI. Local `dotnet restore` still updates the lock file when dependencies change.

> **Note:** Lock files (`packages.lock.json`) must be generated and committed as part of Phase 1 before CI can use `--locked-mode`. See Phase 1 checklist.

---

## 10. NuGet Caching Notes

The cache key includes both `packages.lock.json` (exact dependency versions) and `Directory.Packages.props` (version pins). Either file changing invalidates the cache correctly.

```yaml
key: nuget-${{ runner.os }}-${{ hashFiles('**/packages.lock.json', '**/Directory.Packages.props') }}
restore-keys: |
  nuget-${{ runner.os }}-
```

The `restore-keys` fallback allows the cache to be partially reused when only some packages change — a significant time saving when adding new NuGet references.

---

## 11. `PublishSingleFile` Considerations

The self-contained publish uses `/p:PublishSingleFile=true`. When single-file publish is active:

- Managed assemblies are bundled into the host executable.
- **Native shared libraries are NOT bundled by default.** Adding `/p:IncludeNativeLibrariesForSelfExtract=true` causes native libraries to be extracted to a temp directory at startup and loaded from there.
- The extraction path is `~/.net/<appname>/<hash>/` on Linux/macOS and `%TEMP%\.net\<appname>\<hash>\` on Windows.
- `NativeLibrary.Load()` still works because the runtime updates the native library search path before managed code runs.
- Set `/p:EnableCompressionInSingleFile=true` to reduce binary size (requires .NET 6+; no extra dependencies).

If single-file extraction of native libraries is undesirable (e.g., restricted write access), publish with `/p:PublishSingleFile=false` and distribute as a directory instead. The `mgfxc` binary in the publish output will still be self-contained; only the packaging changes.

---

## 12. Numbered Task Checklist

Execute these steps in order. Each step is independently verifiable.

### 12.1 Native binary versioning

1. - [ ] Create `tools/native-versions.json` with correct SPIRV-Cross release tag and placeholder SHA-256 values (Section 3.1).
2. - [ ] Download each SPIRV-Cross artifact manually and compute actual SHA-256 hashes.
3. - [ ] Fill in all four SHA-256 values in `tools/native-versions.json`.
4. - [ ] Create `tools/restore.sh` with hash verification logic (Section 3.2). Run `chmod +x tools/restore.sh`.
5. - [ ] Create `tools/restore.ps1` with hash verification logic (Section 3.2).
6. - [ ] Run `./tools/restore.sh` on Linux or macOS. Verify `tools/spirv-cross/libspirv-cross-c-shared.so` (or `.dylib`) exists and is executable.
7. - [ ] Run `.\tools\restore.ps1` on Windows. Verify `tools\spirv-cross\spirv-cross-c-shared.dll` exists.
8. - [ ] Add `tools/spirv-cross/` to `.gitignore` (already present from Phase 1 — confirm it is there).

### 12.2 Native binary bundling

9. - [ ] Add `$(RepoRoot)` property to `Directory.Build.props` (Section 4.1).
10. - [ ] Add `<Content Include>` items for all four RIDs to `src/ShadowDusk.GLSL/ShadowDusk.GLSL.csproj` (Section 4.1).
11. - [ ] Create `src/ShadowDusk.GLSL/SpirvCrossNativeLoader.cs` with `NativeLibrary.Load()` logic (Section 4.2).
12. - [ ] Verify `dotnet build -r linux-x64` copies `libspirv-cross-c-shared.so` into `bin/Release/net8.0/linux-x64/runtimes/linux-x64/native/`.
13. - [ ] Verify `dotnet build -r win-x64` copies `spirv-cross-c-shared.dll` into the correct `runtimes/win-x64/native/` path.

### 12.3 NuGet lock files

14. - [ ] Add `RestorePackagesWithLockFile` and `RestoreLockedMode` to `Directory.Build.props` (Section 9).
15. - [ ] Run `dotnet restore --use-lock-file` from repo root. Verify `packages.lock.json` files are created in each project directory.
16. - [ ] Stage and commit all `packages.lock.json` files.

### 12.4 CI workflow

17. - [ ] Create `.github/workflows/` directory if it does not exist.
18. - [ ] Create `.github/workflows/ci.yml` from Section 6.
19. - [ ] Push to a feature branch and open a PR against `main`. Verify the CI workflow triggers.
20. - [ ] Confirm all three OS matrix jobs turn green.
21. - [ ] Confirm the integration tests job triggers on push to `main` (not on the PR itself unless the label is applied).
22. - [ ] Review the test result `.trx` artifacts in the Actions run summary.

### 12.5 Release workflow

23. - [ ] Create `.github/workflows/release.yml` from Section 7.
24. - [ ] Add `NUGET_API_KEY` to repository secrets (Settings → Secrets → Actions).
25. - [ ] Create a test release tag: `git tag v0.1.0-alpha && git push origin v0.1.0-alpha`.
26. - [ ] Confirm all four RID publish jobs complete and produce archives.
27. - [ ] Confirm each archive contains a binary named `mgfxc` (or `mgfxc.exe` on Windows).
28. - [ ] Confirm the NuGet pack + push job completes and the package appears on nuget.org (or confirm `--skip-duplicate` exits 0 if already pushed).
29. - [ ] Confirm the GitHub Release is created with all six assets (four binary archives + `.nupkg` + `.snupkg` if symbols are enabled).

### 12.6 Verification smoke tests

30. - [ ] On each OS, extract the self-contained archive and run `./mgfxc --help` (or `mgfxc.exe --help`). Verify it exits with the mgfxc-compatible exit code and prints usage.
31. - [ ] On Linux, confirm no WINE dependency: `ldd publish/linux-x64/mgfxc | grep -v "not found"` must show no unresolved symbols.
32. - [ ] On macOS, confirm the binary is not quarantined: `xattr -l publish/osx-x64/mgfxc` should not show `com.apple.quarantine` (expected in CI; would appear only on a downloaded artifact).

---

## 13. Acceptance Criteria

| Criterion | How to Verify |
|---|---|
| `dotnet build` green on all 3 OS | All matrix jobs in `ci.yml` exit 0 |
| Unit tests pass on all 3 OS | `dotnet test --filter "Category!=Integration"` exits 0 on all runners |
| Integration tests pass on all 3 OS | Integration jobs in `ci.yml` green (triggered on push to `main`) |
| Self-contained binaries for all 4 RIDs | Four publish jobs in `release.yml` all complete; archives present in GitHub Release |
| Binary named `mgfxc` / `mgfxc.exe` | Verify step in each publish job; manual extraction check (task 30) |
| SPIRV-Cross `.so`/`.dylib` correctly bundled | `dotnet build -r linux-x64` places `.so` in `runtimes/linux-x64/native/` |
| Native library permissions set | `chmod +x` step in every Linux/macOS job; `restore.sh` also sets them |
| Hash verification of native binaries | `restore.sh` / `restore.ps1` both abort on hash mismatch |
| NuGet package published on tag push | Package appears on nuget.org with correct version after `v*.*.*` tag |
| No WINE, no Windows-only paths | `grep -r wine .github/` returns nothing; grep confirms no `fxc.exe` references |
| Deterministic CI (locked NuGet) | `dotnet restore --locked-mode` in all jobs; `packages.lock.json` committed |

---

## 14. Known Pitfalls and Mitigations

| Pitfall | Mitigation |
|---|---|
| `.so`/`.dylib` missing execute bit after cache restore | Unconditional `chmod +x` step after every cache action (Section 8) |
| `dotnet restore --locked-mode` fails after adding a new package | Re-generate lock files locally (`dotnet restore --use-lock-file`) and commit |
| `osx-arm64` publish fails on Intel runner | `dotnet publish -r osx-arm64` from an Intel macOS runner is supported — the .NET SDK cross-compiles managed code; only native code needs the ARM slice (bundled in the SPIRV-Cross `osx-arm64` artifact) |
| SPIRV-Cross archive structure changes between releases | `--strip-components=1` in `tar` and the `Expand-Archive` approach assume a flat or single-directory archive; verify the structure of each release manually when bumping the version pin |
| `PublishSingleFile` extraction fails on read-only file systems | Document this limitation; offer a non-single-file publish variant for containerized environments |
| `cache-hit` detection for native tools | The `id: cache-native` / `steps.cache-native.outputs.cache-hit` pattern is correct; do not use `if: steps.cache-native.outputs.cache-hit == 'false'` — the output is the string `'true'` or absent, not boolean `false` |
| GitHub `macos-latest` runner architecture | As of 2024-25 `macos-latest` is ARM (Apple Silicon). Both `osx-x64` and `osx-arm64` publish jobs use `macos-latest`; `osx-x64` is a cross-publish from ARM. If this causes issues, pin `macos-13` (Intel) for `osx-x64`. |

---

## 15. Known Gaps (Deferred)

| Gap | Notes |
|---|---|
| macOS code signing and notarization | Required for Gatekeeper pass on end-user machines; out of scope for CI phase |
| Windows Authenticode signing | Required for `SmartScreen` bypass; out of scope |
| `linux-arm64` RID | GitHub-hosted ARM Linux runners require paid minutes; add when budget allows |
| Symbol packages (`.snupkg`) | Add `/p:IncludeSymbols=true /p:SymbolPackageFormat=snupkg` to `dotnet pack` and push to NuGet.org |
| Automatic release notes | Consider `release-drafter` or `github-changelog-generator` |
| Container image publish | A Docker image with `mgfxc` baked in would simplify CI for MonoGame game projects |
| Dependabot for NuGet and GitHub Actions | Add `.github/dependabot.yml` to get automated version bump PRs |
