# Phase 30 — Cross-Platform CI, NuGet Release & `/release` Automation (GitHub Actions)

> **What this phase covers (read first — the old name "Cross-Platform CI" hid half of it):** three things, not one.
> 1. **CI** — build + test on Linux/macOS/Windows on every push/PR (`ci.yml`).
> 2. **NuGet release** — pack **all six** `ShadowDusk.*` packages + the `mgfxc` tool, push to nuget.org, and attach self-contained CLI binaries to a GitHub Release, on a `v*.*.*` tag *or* a manual dispatch (`release.yml`).
> 3. **`/release` automation** — a one-command release skill (`.claude/skills/release/SKILL.md`, modelled on the KernSmith `/release`) that bumps the **single** centralized version, updates `CHANGELOG.md`/`RELEASING.md`, opens the PR, waits for CI, merges, and triggers publish. See **§17**.
>
> The CI half can ship independently; the version-centralization in **§17.1** is a prerequisite for both the `/release` skill and the release workflow's tag↔version validation.

## Overview

This phase wires up GitHub Actions CI that builds, tests, and packages ShadowDusk across Linux, macOS, and Windows — **and runs the WASM build + headless-browser validation** that Phases 22/23/24 depend on — **and the NuGet release pipeline + `/release` skill** that turn a green `main` into published packages with one command. It validates every prior phase end-to-end in a clean, reproducible environment, and is where **Part-1 (reach)** is proven: the desktop GL + vkd3d-DXBC paths *run* on Linux/macOS, and the WASM path *renders* in a headless browser.

**Inputs:** A complete, passing local build from Phases 1–9, plus the WASM engine (Phase 19) and browser harness (Phase 24). **Prerequisite for the release half:** the hermetic-restore `nuget.config` (PR #8, central package management) merged, so CI `dotnet restore`/`pack` resolves identically regardless of machine feeds.
**Outputs:** Green CI badges on every push/PR; **all six `ShadowDusk.*` NuGet packages + the `mgfxc` dotnet tool** published to nuget.org and self-contained CLI binaries attached to a GitHub Release on every `v*.*.*` tag (or manual dispatch); a headless-browser smoke that renders corpus shaders in KNI WebGL; and a `/release` skill that drives the whole cut.

> **This phase OWNS the browser/WASM CI** that Phases 22, 23, 24, and 100 all defer to it. See **§16 — WASM & Headless-Browser CI** below; if that section is empty, those deferrals are unsatisfied.
> **This phase also OWNS NuGet release + the `/release` skill.** See **§17**. Phase 27 (pre-1.0 sweep) and any "ship 1.0" work depend on §17 existing.

---

## Scope and Non-Goals

**In scope:**
- `ci.yml` — build + unit test matrix on all 3 OS (push/PR)
- `release.yml` — build + test + self-contained publish + **multi-package NuGet pack & push** on `v*.*.*` tags **and `workflow_dispatch`** (§17.3)
- **Centralizing the package version** into one `Directory.Build.props` `<Version>` (§17.1 — the "correct way"; removes the six scattered, confusingly-named `<PackageVersion>` properties)
- **Packing all six `ShadowDusk.*` packages**, not just the CLI (§17.2): `Core`, `HLSL`, `GLSL`, `Compiler`, `Cli` (`mgfxc` tool), `Wasm`
- **`CHANGELOG.md` + `RELEASING.md`** (§17.4) and the **`/release` skill** (§17.5)
- Native binary restore and caching for **SPIRV-Cross *and* vkd3d-shader** (the DirectX DXBC backend, Phase 18)
- `chmod +x` fix for native shared libraries on Linux/macOS
- RID matrix covering all 4 targets
- Hash verification of all downloaded/built native binaries
- **WASM build job** (`wasm-tools` workload, emscripten 3.1.34 pin) + **headless-browser render smoke** (Phase 24's Playwright harness) — see §14

**Out of scope:**
- macOS code signing / notarization (future work)
- Windows code signing (future work)
- Docker-based Linux builds (GitHub-hosted runners are sufficient)
- ARM Linux runners (not available on GitHub free tier; add later)
- Fully auto-generated changelog bodies (the `/release` skill curates `CHANGELOG.md` by hand; GitHub's `generate_release_notes` supplements the Release page only)
- Publishing the `ShadowDusk.MgcbPlugin` / `ShadowDusk.Metal` packages (still stubs — Phases 29/31)

---

## 1. RID Matrix

Each RID needs **both** native backends bundled: SPIRV-Cross (OpenGL path) and **vkd3d-shader** (DirectX DXBC path, Phase 18 — the cross-platform shipping DX backend, so the DirectX half of "reach" is CI-validated too).

| RID | Hosted Runner OS | SPIRV-Cross | vkd3d-shader (DXBC) | Notes |
|---|---|---|---|---|
| `win-x64` | `windows-latest` | `spirv-cross` win-x64 | `libvkd3d-shader.dll` (+ `d3dcompiler_47` oracle, ships w/ Windows) | Primary dev platform; DXC via Vortice.Dxc NuGet |
| `linux-x64` | `ubuntu-latest` | `spirv-cross` linux-x64 | `libvkd3d-shader.so` | CI + server deployments; `.so`/`.dylib` need `chmod +x` |
| `osx-x64` | `macos-latest` | `spirv-cross` osx-x64 | `libvkd3d-shader.dylib` | Intel Mac legacy; cross-publish |
| `osx-arm64` | `macos-latest` | `spirv-cross` osx-arm64 | `libvkd3d-shader.dylib` (arm64) | M1/M2/M3; cross-publish |

> **vkd3d-shader is built from source** (vkd3d-1.17, WineHQ, MSYS2/autotools on Windows; distro/source on Linux/macOS) per the `tools/restore.*` recipe (Phase 18) — there is no official prebuilt Windows DLL. This is the DirectX-reach validation that Phase 18 carried forward to CI. **`MEMORY: DX vkd3d not packaged yet`** — packaging it into the per-RID publish is part of this phase.

> **Note:** `osx-arm64` self-contained publish runs on the macOS Apple Silicon runner with `-r osx-arm64`. GitHub's `macos-latest` is Apple Silicon (M1/M2) as of 2025. Both `osx-x64` and `osx-arm64` use `macos-latest`; `osx-x64` is a cross-publish. If Intel is required, pin `macos-13`.

---

## 2. Repository Secrets Required

| Secret | Used in | Purpose |
|---|---|---|
| `NUGET_API_KEY` | `release.yml` | Push to `api.nuget.org` |

Configure via **Settings → Secrets and variables → Actions → New repository secret**.

---

## 3. Native Binary Versioning

> **⚠️ As-built update (post-Phase-18):** the `native-versions.json` + GitHub-release-download model below was the *green-field plan*. The **as-built** native strategy moved to **building from source via `tools/restore.*`** (SPIRV-Cross matched to the `Silk.NET.SPIRV.Cross.Native` pinned commit; **vkd3d-shader** from WineHQ source; and the WASM `spirv-cross.wasm`/`dxcompiler.wasm` via emscripten 3.1.34) because Khronos/WineHQ do not publish prebuilt binaries for every platform. **Treat `native-versions.json` as a pinning/hash manifest the restore scripts consult, not a URL-download index.** The integrity discipline (pin version, SHA-256 verify) still applies — to the built/restored artifacts. The original plan is kept below for the hash-manifest shape.

All native binary versions and hashes are pinned in a single file:

```
tools/native-versions.json
```

### 3.1 `tools/native-versions.json` format

> **IMPORTANT: Verify SPIRV-Cross artifact availability before filling in URLs.** KhronosGroup does not publish pre-built `libspirv-cross-c-shared` binaries for all platforms on every SPIRV-Cross release. The Phase 6 SPIRV-Cross integration used binaries that are already present in `tools/spirv-cross/` — check where those came from (likely the Vulkan SDK or a custom build) and use the same source for the restore scripts. If no suitable prebuilt release exists, the SPIRV-Cross C shared library will need to be compiled from source as part of CI setup, or hosted in a dedicated artifact store (e.g. a GitHub release in this repo). The URLs below are placeholder examples.

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
>   <RepoRoot>$([MSBuild]::NormalizeDirectory($([MSBuild]::GetDirectoryNameOfFileAbove($(MSBuildProjectDirectory), 'ShadowDusk.slnx'))))</RepoRoot>
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
        run: dotnet restore ShadowDusk.slnx --locked-mode

      # ── 8. Build ──────────────────────────────────────────────────────────────
      - name: dotnet build
        run: dotnet build ShadowDusk.slnx --no-restore -c Release

      # ── 9. Unit tests ─────────────────────────────────────────────────────────
      - name: dotnet test (unit)
        run: >
          dotnet test ShadowDusk.slnx --no-build -c Release
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
        run: dotnet restore ShadowDusk.slnx --locked-mode

      - name: dotnet build
        run: dotnet build ShadowDusk.slnx --no-restore -c Release

      - name: dotnet test (integration)
        run: >
          dotnet test ShadowDusk.slnx --no-build -c Release
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

> **⚠️ Reconcile with §17 before implementing.** The YAML below was the original green-field draft and has **two gaps §17 closes**:
> 1. Its `nuget` job packs **only `src/ShadowDusk.Cli`**. The product is the **six-package `ShadowDusk.*` set** — it must pack all six (§17.2). Replace the single `dotnet pack` with the matrix/loop in §17.2.
> 2. It has **no `workflow_dispatch`** and **no tag↔version validation**. Add both (§17.3) so a release can be triggered from the Actions UI and so a `v1.2.3` tag whose `Directory.Build.props` says `1.2.0` fails fast instead of publishing a mislabeled package.
>
> Treat §17.2/§17.3 as the authoritative spec for the `nuget` job and the workflow triggers; the block below is correct for the build/publish/`github-release` jobs.

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
        run: dotnet restore ShadowDusk.slnx --locked-mode

      - name: dotnet build
        run: dotnet build ShadowDusk.slnx --no-restore -c Release

      - name: dotnet test (unit + integration)
        run: >
          dotnet test ShadowDusk.slnx --no-build -c Release
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
          # Pass the target RID explicitly so restore.sh downloads the correct
          # SPIRV-Cross artifact when cross-publishing (e.g. osx-arm64 from Apple Silicon).
          SPIRV_CROSS_RID: ${{ matrix.rid }}
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
        run: dotnet restore ShadowDusk.slnx --locked-mode

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
        run: dotnet restore ShadowDusk.slnx --locked-mode

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

> **Note:** Lock files (`packages.lock.json`) must be generated and committed **before the first CI run**. They were not required in Phases 1–9. This phase must generate them as a prerequisite step (checklist task 12.3). Until lock files exist, use `dotnet restore ShadowDusk.slnx` (without `--locked-mode`) for local development. Add `--locked-mode` only after the lock files are committed.

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
| ~~Symbol packages (`.snupkg`)~~ | **Now in scope — §17.2** adds `/p:IncludeSymbols=true /p:SymbolPackageFormat=snupkg` to the six-package pack |
| ~~Automatic release notes~~ | **Now addressed — §17.4** (`CHANGELOG.md` curated by `/release`) + GitHub `generate_release_notes` on the Release page |
| Container image publish | A Docker image with `mgfxc` baked in would simplify CI for MonoGame game projects |
| Dependabot for NuGet and GitHub Actions | Add `.github/dependabot.yml` to get automated version bump PRs |

> **Note:** the WASM build + headless-browser validation is **no longer a deferred gap** — it is owned here, in §16.

---

## 16. WASM & Headless-Browser CI (owns the deferrals from Phases 22/23/24/100)

Phases 22, 23, 24, and 100 all defer their browser/WASM validation "to Phase 30." This section discharges that. It is **separate from the desktop matrix** (different toolchain, slower, browser-gated) and lands as its own workflow job(s).

### 16.1 WASM build job

- [ ] Install the WASM workload: `dotnet workload install wasm-tools`.
- [ ] **Pin emscripten to 3.1.34** (the .NET 8 runtime's version — a mismatch fails at link/load, not cleanly; see Phase 23 §Hard constraints). The `wasm-tools` workload carries it; any *native* emscripten build step (SPIRV-Cross, the faithful DXC→WASM module) must use the same 3.1.34 via `tools/restore.*`.
- [ ] Build `samples/ShaderFiddle.Web` (and the multi-targeted `ShadowDusk.Wasm`) for `net8.0-browser`; `dotnet publish -c Release`; confirm `_framework/*.wasm` + the `shadowdusk-dxc`/`shadowdusk-spirv-cross` static web assets land in publish output.
- [ ] Restore the WASM native artifacts (`spirv-cross.wasm`; the faithful `dxcompiler.wasm` once Phase 23 M0 produces it) via `tools/restore.*`, **SHA-256-verified** (Phase 25 supply-chain discipline applies to `.wasm` artifacts).
- [ ] Node per-stage byte-identity checks (already in `.wasm-build/`): SPIRV-Cross (and, post-M0, DXC→WASM) WASM output == desktop, on the corpus.

### 16.2 Headless-browser render smoke (Phase 24's harness)

- [ ] Install Playwright browsers (`playwright install --with-deps chromium`).
- [ ] Run **[Phase 24](DONE/PHASE-24-browser-render-validation.md)**'s harness headless against the published sample: **mode-1** (precompiled `.mgfx` loads + renders in KNI WebGL — the MGFXReader10/KNIFX-v11 answer), then **mode-2** (in-browser compile + render).
- [ ] **KNI HiDef / WebGL2 run (Phase 33 — issue #7 regression guard):** `node publish-sample-sd-hidef.mjs` then `node run-harness.mjs --corpus=sd-hidef` (boots the sample with `?profile=hidef` → WebGL2 / GLSL ES 3.00). This is the **continuous guard for issue #7** — a regression to a raw-`gl_FragColor` write would flip it RED. After the Phase 33 fix it is GREEN (`RESULTS-SD-HIDEF.md`: 10/10 load + render); the harness writes `RESULTS-SD-HIDEF-REPRO.md` instead if it ever fails. Also run the matching Reach baseline (`--corpus=sd`) as the no-regression check.
- [ ] Use deterministic software GL (`--use-gl=angle --use-angle=swiftshader`) so pixel comparison is reproducible across runners.
- [ ] Pixel-compare against Phase-17 references at the **§6.1 tolerance** (shared standard — do not invent a new one).
- [ ] **AV-scan slowness allowance:** apply the CLAUDE.md Phase 21 note (freshly-built native/WASM binaries get on-access-scanned cold); generous step timeouts, and exclude `**/bin`, `**/obj`, `tools/`, `.wasm-build/` on self-hosted runners.

### 16.3 Gating

- [ ] Browser job runs on push to `main` and on PRs labelled `run-browser` (it is slow + heavy); never blocks the fast unit matrix.
- [ ] Once Phase 23 M0 (faithful DXC→WASM) lands, the mode-2 smoke asserts **byte-identical-to-CLI `.mgfx`** (the faithful-path proof), not just "renders."

### 16.4 Acceptance (browser/WASM additions to §13)

| Criterion | How to Verify |
|---|---|
| WASM build succeeds | `dotnet publish` of `ShaderFiddle.Web` (net8.0-browser) exits 0 with `wasm-tools` + emscripten 3.1.34 |
| vkd3d-shader bundled per RID | `dotnet build -r linux-x64` places `libvkd3d-shader.so` in `runtimes/linux-x64/native/`; DX `.mgfx` compiles on Linux/macOS (Phase 18 reach) |
| Mode-1 renders in KNI WebGL | Phase 24 harness: 10/10 corpus `.mgfx` load + render pixel-equivalent in headless Chromium |
| KNI HiDef/WebGL2 loads (issue #7) | Phase 33 harness `--corpus=sd-hidef`: 10/10 corpus `.mgfx` load with **no GLSL error** + render within tolerance in headless KNI HiDef (WebGL2 / GLSL ES 3.00) |
| Faithful mode-2 (post-M0) | In-browser DXC→WASM `.mgfx` bytes == CLI bytes for a corpus shader |
| `.wasm` supply chain | `.wasm` artifacts SHA-256-verified in `tools/restore.*` |

---

## 17. NuGet Release & the `/release` Skill (owns release automation)

This section is the authoritative spec for **how a ShadowDusk release is cut**: a single centralized version, a multi-package publish, and a `/release` skill that drives it end-to-end. It is modelled on the working **KernSmith `/release`** (a sibling repo of the same author that publishes 8 NuGet packages from a tag), adapted to ShadowDusk's six-package set + native-dependency reality.

> **Why this is here and not its own phase:** the release workflow (`release.yml`) and the CI workflow (`ci.yml`) share the entire native-restore / RID-matrix / locked-restore machinery in §1–§11. Splitting them would duplicate ~300 lines of YAML and two checklists. The `/release` skill is the human-facing front door to the `release.yml` defined in §7 + the additions below.

### 17.1 Centralize the package version (the "correct way" — prerequisite)

**Current state (the problem):** each of the six packable projects hard-codes its own version as an MSBuild **property**:

```
src/ShadowDusk.Core/ShadowDusk.Core.csproj         <PackageVersion>0.1.0</PackageVersion>
src/ShadowDusk.HLSL/ShadowDusk.HLSL.csproj         <PackageVersion>0.1.0</PackageVersion>
src/ShadowDusk.GLSL/ShadowDusk.GLSL.csproj         <PackageVersion>0.1.0</PackageVersion>
src/ShadowDusk.Compiler/ShadowDusk.Compiler.csproj <PackageVersion>0.1.0</PackageVersion>
src/ShadowDusk.Cli/ShadowDusk.Cli.csproj           <PackageVersion>0.1.0</PackageVersion>
src/ShadowDusk.Wasm/ShadowDusk.Wasm.csproj         <PackageVersion>0.1.0</PackageVersion>
```

Two things are wrong with this:

1. **Six edits per release, easy to desync.** A `/release` skill (or a human) must touch six files and can leave one behind, shipping a mixed-version set where `ShadowDusk.Compiler 0.2.0` depends on `ShadowDusk.HLSL 0.1.0`.
2. **The property name collides with Central Package Management.** This repo uses **CPM** (`Directory.Packages.props`, `ManagePackageVersionsCentrally=true`; see PR #8 from @vchelaru on the hermetic-restore `nuget.config`). In CPM, `<PackageVersion Include="X" Version="Y" />` is an **item** that pins a *dependency's* version. The per-csproj `<PackageVersion>0.1.0</PackageVersion>` is a **property** that sets *this package's output* version. They are different MSBuild constructs that happen to share a name — a genuine footgun that makes the project look like it is "doing versioning wrong." This is the discrepancy that prompted the request.

**Fix:** make `Directory.Build.props` the single source of truth, exactly like KernSmith.

```xml
<!-- Directory.Build.props -->
<PropertyGroup>
  <!-- Single source of truth for ALL ShadowDusk.* package versions.
       /release bumps this one line; release.yml validates the tag against it. -->
  <Version>0.1.0</Version>
</PropertyGroup>
```

Then **delete the six `<PackageVersion>…</PackageVersion>` property lines** from the csprojs. With CPM on, `<Version>` flows to every project; `dotnet pack` uses it for `PackageVersion` automatically. Keep the per-project `<PackageId>`, `<Description>`, `<PackageTags>`, etc. — only the version centralizes.

> **Verification after centralizing:** `dotnet pack ShadowDusk.slnx -c Release -o nupkg` must emit `ShadowDusk.Core.0.1.0.nupkg`, `…HLSL.0.1.0.nupkg`, … all six at the same version, and the inter-package `<dependency>` entries inside each `.nuspec` must reference `[0.1.0, )` (not a stale `0.1.0` literal). Confirm with `unzip -p nupkg/ShadowDusk.Compiler.0.1.0.nupkg '*.nuspec'`.

### 17.2 Pack all six packages (not just the CLI)

The product is the **set**, not the CLI alone (the CLI is one delivery shape — see CLAUDE.md *THE PURPOSE*). A consumer runs `dotnet add package ShadowDusk.Compiler` and NuGet must restore `Core/HLSL/GLSL` + the native deps transitively. So `release.yml`'s `nuget` job must pack **all six**:

| Package | Project | Notes |
|---|---|---|
| `ShadowDusk.Core` | `src/ShadowDusk.Core` | net8.0 |
| `ShadowDusk.HLSL` | `src/ShadowDusk.HLSL` | net8.0; pulls `Vortice.Dxc` (cross-platform DXC native) transitively |
| `ShadowDusk.GLSL` | `src/ShadowDusk.GLSL` | net8.0; pulls `Silk.NET.SPIRV.Cross.Native` transitively |
| `ShadowDusk.Compiler` | `src/ShadowDusk.Compiler` | net8.0; **the consumer-facing product**; depends on the three above |
| `ShadowDusk.Cli` | `src/ShadowDusk.Cli` | `PackAsTool` → `dotnet tool install -g ShadowDusk.Cli` gives `mgfxc` |
| `ShadowDusk.Wasm` | `src/ShadowDusk.Wasm` | **net8.0-browser** — must be packed on a job with the `wasm-tools` workload + the restored `dxcompiler.wasm` (its csproj `VerifyDxcWasmPresent` target hard-fails the pack otherwise). Pack it in the **§16 WASM job**, not the ubuntu `nuget` job. |

Replace the single-CLI `dotnet pack` in §7's `nuget` job with (version comes from the validated tag, §17.3):

```yaml
- name: Pack desktop packages
  run: |
    for proj in \
      src/ShadowDusk.Core/ShadowDusk.Core.csproj \
      src/ShadowDusk.HLSL/ShadowDusk.HLSL.csproj \
      src/ShadowDusk.GLSL/ShadowDusk.GLSL.csproj \
      src/ShadowDusk.Compiler/ShadowDusk.Compiler.csproj \
      src/ShadowDusk.Cli/ShadowDusk.Cli.csproj ; do
        dotnet pack "$proj" -c Release -o nupkg \
          /p:Version=${{ needs.validate.outputs.version }} \
          /p:IncludeSymbols=true /p:SymbolPackageFormat=snupkg
    done
# ShadowDusk.Wasm is packed in the WASM job (§16) and its .nupkg uploaded as an
# artifact; the push step globs all nupkg/*.nupkg from both jobs.
```

The existing `dotnet nuget push nupkg/*.nupkg … --skip-duplicate` step then pushes the whole set (idempotent — re-running a release no-ops on already-published versions).

> **Known native-packaging gap (carry-forward from `MEMORY: DX vkd3d not packaged yet`):** the **GL + DXC in-memory path is fully self-contained from NuGet today** — `Vortice.Dxc` and `Silk.NET.SPIRV.Cross.Native` are on nuget.org and ride transitively, so `dotnet add package ShadowDusk.Compiler` → compile `.fx` → GL `.mgfx` works on a clean machine. The **DirectX DXBC backend's `vkd3d-shader` native is NOT yet packaged** (it is a restored, non-redistributed artifact). Until it ships as a `runtimes/<rid>/native/` asset inside `ShadowDusk.HLSL` (or a dedicated `ShadowDusk.Native.Vkd3d` package), DX-in-memory from a pure NuGet add is not self-contained. Packaging it is tracked in §4-style bundling and listed in §17.6 as a release blocker **only for advertising DX support** — the 0.1.x line can ship GL-only-from-NuGet honestly.

### 17.3 Triggers + the `validate` job (tag ↔ version guard)

Mirror KernSmith: `release.yml` fires on **either** a `v*` tag push **or** a manual `workflow_dispatch` with a `version` input, and a `validate` job gates everything else.

```yaml
on:
  push:
    tags: ['v*.*.*']
  workflow_dispatch:
    inputs:
      version:
        description: 'Version to release (e.g., 0.2.0)'
        required: true
        type: string

jobs:
  validate:
    runs-on: ubuntu-latest
    outputs:
      version: ${{ steps.resolve.outputs.version }}
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0 }
      - name: Resolve version (input or tag, strip leading 'v')
        id: resolve
        run: |
          if [ -n "${{ inputs.version }}" ]; then
            echo "version=${{ inputs.version }}" >> "$GITHUB_OUTPUT"
          else
            echo "version=${GITHUB_REF_NAME#v}" >> "$GITHUB_OUTPUT"
          fi
      - name: Verify version matches Directory.Build.props
        run: |
          want="${{ steps.resolve.outputs.version }}"
          have=$(grep -oP '(?<=<Version>)[^<]+' Directory.Build.props)
          if [ "$have" != "$want" ]; then
            echo "::error::Release version ($want) != Directory.Build.props ($have). Merge a version-bump PR first (use /release)."
            exit 1
          fi
```

Every downstream job gains `needs: validate` and reads `${{ needs.validate.outputs.version }}`. On `workflow_dispatch`, a final `create-release` step also creates and pushes the `v<version>` tag (so the GitHub Release anchors to a tag even when triggered from the UI) — see KernSmith's `create-release` job for the exact pattern.

### 17.4 `CHANGELOG.md` + `RELEASING.md`

Neither exists yet. Both are prerequisites for `/release`.

- **`CHANGELOG.md`** — [Keep a Changelog](https://keepachangelog.com) format with a rolling `## [Unreleased]` section at the top (`### Added` / `### Changed` / `### Fixed`). `/release` moves `[Unreleased]` into a dated `## [<version>] - <YYYY-MM-DD>` section. Seed it with a `## [0.1.0]` entry summarizing Phases 1–24 (the current shipped capability: cross-platform GL + DX compile, in-memory + CLI + WASM sample).
- **`RELEASING.md`** — the human-readable runbook the skill automates: prerequisites (`NUGET_API_KEY` secret, nuget.org owner rights on all six IDs), the one-line version bump, how to trigger `release.yml` (tag vs. dispatch), and what to verify on nuget.org afterward. Keep version examples current (the skill refreshes them).

### 17.5 The `/release` skill (`.claude/skills/release/SKILL.md`)

Adapt KernSmith's skill. Below is the spec to drop in — note the ShadowDusk-specific deltas called out after it.

```markdown
---
name: release
description: "Cut a ShadowDusk release: bump the single centralized version, update CHANGELOG/RELEASING, audit docs, build+test, commit, push, PR, wait for CI, merge, then trigger the NuGet publish. Trigger on 'release', 'cut a release', 'bump version', 'new version', or /release."
argument-hint: "<version> (e.g., 0.2.0)"
---

# Release

Automate the full ShadowDusk release from version bump through PR merge to publish trigger.

## Input
`$ARGUMENTS` is the version (e.g., `0.2.0`). If omitted, read the current `<Version>` from
`Directory.Build.props` and ask the user for the new one.

## Steps

1. **Validate clean tree.** `git status`; if dirty, warn and stop. Show current `<Version>`.
2. **Branch from latest main.** `git checkout main && git pull && git checkout -b version/<version>`.
3. **Bump version.** Edit `Directory.Build.props` `<Version>` only — the single source of truth
   (do NOT touch the six csprojs; per §17.1 they no longer carry a version). 
4. **Update CHANGELOG.md.** Move `[Unreleased]` → `## [<version>] - <today YYYY-MM-DD>`; leave a fresh
   empty `[Unreleased]`. If Unreleased is empty, add "- Version bump and documentation updates".
5. **Update RELEASING.md** version examples to <version>.
6. **Docs audit (Explore agent, report-only — do NOT auto-fix).** Check CLAUDE.md inventory,
   root README, CLI README, the DocFX site (Phase 26), and each csproj <Description> against the
   code. Report gaps; ask whether to fix now or defer.
7. **Build + test.** `dotnet build ShadowDusk.slnx -c Release` then
   `dotnet test ShadowDusk.slnx -c Release --no-build --settings ShadowDusk.runsettings`
   (the runsettings 5-min TestSessionTimeout — see CLAUDE.md Phase 21). Stop on failure.
8. **Commit.** Stage the release files only; conventional message. NO Co-Authored-By / tool-attribution
   trailer of any kind (CLAUDE.md Git Commit Conventions).
9. **Push + PR.** `git push -u origin version/<version>`; `gh pr create` with a summary-bullets body
   (no test-plan section, no tool-attribution footer).
10. **Wait for PR CI.** `gh pr checks <pr> --watch`. Do not merge on red. Local green is not enough —
    CI runs the 3-OS matrix (§6).
11. **Merge.** `gh pr merge <pr> --merge`.
12. **Wait for post-merge main CI**, then trigger publish: tell the user to run
    **Actions → Release → Run workflow** with version `<version>`, or
    `git tag v<version> && git push origin v<version>`. The `validate` job (§17.3) checks the tag
    against Directory.Build.props, then all six packages + `mgfxc` publish to nuget.org and a GitHub
    Release is cut.

## Edge cases
Dirty tree → stop. Branch exists → ask. Empty Unreleased → minimal entry. Merge conflict → stop, don't force.
```

**ShadowDusk-specific deltas from the KernSmith skill:**
- **One file bumps**, not `Directory.Build.props`-as-property-bag plus csprojs — because §17.1 centralizes.
- **No `/commit` skill exists here** — the skill commits directly, obeying CLAUDE.md (selective staging, **no `Co-Authored-By` of any kind**, no "Generated with Claude Code").
- **Test invocation passes `--settings ShadowDusk.runsettings`** (the Phase 21 suite-timeout guardrail), matching the `/test` skill.
- **Docs audit targets ShadowDusk's doc set**: CLAUDE.md inventory, the Phase 26 DocFX site, the WASM HOWTO, per-package `<Description>`s.
- **Publish trigger is the §17.3 workflow** (tag or dispatch), which validates the tag against the centralized `<Version>`.

### 17.6 Numbered Task Checklist (release automation)

#### 17.6.a Versioning + packaging
33. - [ ] Merge PR #8 (`nuget.config`, hermetic restore) — prerequisite for deterministic CI pack/restore.
34. - [ ] Add `<Version>0.1.0</Version>` to `Directory.Build.props`; delete the six per-csproj `<PackageVersion>` **properties** (§17.1). Leave `Directory.Packages.props` `<PackageVersion Include=…>` **items** untouched.
35. - [ ] `dotnet pack ShadowDusk.slnx -c Release -o nupkg` emits all six `.nupkg` at one version; inter-package deps resolve (§17.1 verification).
36. - [ ] Confirm a clean-machine `dotnet add package ShadowDusk.Compiler` restores `Core/HLSL/GLSL` + `Vortice.Dxc` + `Silk.NET.SPIRV.Cross.Native` and compiles a `.fx` → GL `.mgfx` in memory (the self-contained promise, GL path).

#### 17.6.b Release workflow
37. - [ ] Add `workflow_dispatch` + the `validate` job (tag↔version guard) to `release.yml` (§17.3).
38. - [ ] Replace the single-CLI pack with the six-package pack loop (§17.2); add `IncludeSymbols`/`snupkg`.
39. - [ ] Pack `ShadowDusk.Wasm` in the §16 WASM job (needs `wasm-tools` + restored `dxcompiler.wasm`); upload its `.nupkg` for the shared push.
40. - [ ] `NUGET_API_KEY` secret present (§2) and owner of all six package IDs on nuget.org confirmed (first publish reserves the IDs).

#### 17.6.c `/release` skill + docs
41. - [ ] Create `CHANGELOG.md` (Keep a Changelog) seeded with `[0.1.0]` (Phases 1–24 summary) + empty `[Unreleased]` (§17.4).
42. - [ ] Create `RELEASING.md` runbook (§17.4).
43. - [ ] Create `.claude/skills/release/SKILL.md` from §17.5.
44. - [ ] Dry-run: `/release 0.1.1-alpha` on a scratch branch → PR opens, CI runs, **do not merge**; confirm the skill stops cleanly when asked.
45. - [ ] First real cut: `/release 0.2.0` → merged, dispatch/tag → all six packages + `mgfxc` live on nuget.org at 0.2.0, GitHub Release has the four self-contained CLI archives.

### 17.7 Acceptance Criteria (release automation — additions to §13)

| Criterion | How to Verify |
|---|---|
| Single version source | Only `Directory.Build.props` carries `<Version>`; no `<PackageVersion>` **property** remains in any csproj; `grep -rl '<PackageVersion>' src` returns nothing |
| All six packages publish | After a tag/dispatch, nuget.org shows `ShadowDusk.{Core,HLSL,GLSL,Compiler,Cli,Wasm}` all at `<version>` |
| Tag ↔ version guard works | A `v9.9.9` tag against `Directory.Build.props=0.1.0` fails the `validate` job (does not publish) |
| `mgfxc` tool installs | `dotnet tool install -g ShadowDusk.Cli --version <version>` then `mgfxc --help` works |
| GL in-memory self-contained | Clean-machine consumer test (task 36) compiles a `.fx` with only `dotnet add package ShadowDusk.Compiler` |
| `/release` drives the cut | `/release <version>` performs bump → CHANGELOG → PR → wait-CI → merge → publish-trigger with no manual file edits |
| Conventions honored | Release commits carry **no** `Co-Authored-By` / tool-attribution; PR body has no test-plan/attribution footer |
