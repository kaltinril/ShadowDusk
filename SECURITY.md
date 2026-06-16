# Security Policy

ShadowDusk is a cross-platform HLSL shader compiler: a library (plus its CLI and MGCB
delivery shapes) that a developer adds to their own MonoGame / KNI / FNA project to compile
`.fx` shaders to `.mgfx` / `.fxb` at build time or at runtime. This document states the
threat model honestly, describes the supply-chain hygiene of the native binaries we ship,
and explains how to report a vulnerability.

## The trust model (read this first)

**Compiling a `.fx` runs code.** The shader author and the person running the compile are
the same trust domain: the developer building their game. Compiling a shader is running a
compiler over source you chose to compile, exactly like building C++ or C# you wrote or
copied. Treat shader source the way you treat any other source code: **only compile shaders
you trust, the same way you only build C#/C++ you trust.**

A `.fx` doing "hostile" things to the machine that compiles it (`#include "../../etc/passwd"`,
a giant include that exhausts memory, a huge source string) is the developer compiling code
on their own machine. They can already read their own files and exhaust their own memory; no
privilege boundary is crossed, so there is nothing for the library to defend against. For
that reason ShadowDusk does **not** add input-validation theater (path-traversal blocks,
include/source size caps, macro sanitizing) that would imply it sandboxes untrusted shader
input. It does not.

**ShadowDusk is a developer build-time / in-app tool, not a multi-tenant sandbox.** It does
not isolate the compile from the host and does not claim to make untrusted shader input safe.

### If you accept third-party `.fx` (the consumer's responsibility)

You might choose to build a **public service or in-browser fiddle that compiles strangers'
`.fx`** (the XnaFiddle shape). That crosses a real trust boundary, but it is your
architecture decision and the library cannot own it for you: compiling arbitrary shader
source is running a compiler over attacker-controlled input, and the honest mitigation is
process / host isolation plus resource limits at the service layer. If you accept
third-party shader source:

- Run the compile in an **isolated process or container** with CPU, memory, wall-clock, and
  filesystem limits enforced by the OS / container runtime, not by the library.
- Do **not** point a `FileSystemIncludeResolver` with access to sensitive paths at
  stranger-supplied input. For a no-filesystem host (a browser fiddle), use
  `InMemoryIncludeResolver`, which resolves only from an in-memory dictionary, so there is no
  host filesystem to traverse in the first place.

The library will not do this isolation for you, and you should not assume it does.

## Supply chain of the native binaries we ship

The one place ShadowDusk itself sits in a trust path is the **native binaries it
distributes** (you trust our package). Those are version-pinned and integrity-checked:

- **vkd3d-shader (DXBC / fx_2_0 backend), the macOS DXC dylib, and the WebAssembly modules**
  (DXC to WASM, SPIRV-Cross, vkd3d to WASM) are **version-pinned and SHA-256-verified** by
  `tools/restore.ps1` / `tools/restore.sh` against hashes embedded in those scripts. They are
  downloaded from **fixed GitHub Release tags** (`native-vkd3d-1.17`,
  `native-dxc-1.7.2212.40`, `native-vkd3d-wasm-1.17`); a hash mismatch is rejected and
  re-downloaded rather than used. Packaging runs under **hard release gates**
  (`.github/workflows/release.yml` and `wasm.yml` fail the build if a shipped native is
  missing or mismatched), so a NuGet package cannot ship without its verified natives.
- **The runtime SPIRV-Cross native ships transitively via the versioned
  `Silk.NET.SPIRV.Cross.Native` NuGet package**, with integrity provided by NuGet plus the
  committed `packages.lock.json` files restored under CI `RestoreLockedMode`. (The
  `tools/spirv-cross/` copy is an optional build-from-source convenience pulled from the
  developer's own Vulkan SDK / vcpkg toolchain; it never ships to consumers.)
- **Desktop DXC on Windows/Linux** comes from the `Vortice.Dxc` NuGet package, pinned in
  `Directory.Packages.props` and locked via `packages.lock.json`.

Any native added to ShadowDusk's distribution in the future must join the same
pin + SHA-256 + release-gate discipline. To verify a shipped native yourself, compute its
SHA-256 and compare it against the pinned value in `tools/restore.ps1` / `tools/restore.sh`.

## Reporting a vulnerability

If you believe you have found a security vulnerability in ShadowDusk, please report it
**privately** so it can be addressed before public disclosure:

- **Preferred:** open a private report via GitHub Security Advisories on the repository
  (the **Security** tab -> **Report a vulnerability**) at
  <https://github.com/kaltinril/ShadowDusk/security/advisories/new>.
- **Alternative:** email the maintainer at jeremy.swartwood@gmail.com with details and, if
  possible, a minimal reproduction.

Please do not open a public issue for a suspected vulnerability. We will acknowledge your
report, investigate, and coordinate a fix and disclosure timeline with you. Reports that
amount to "a `.fx` I chose to compile can affect my own machine" fall under the trust model
above and are not vulnerabilities in the product; reports about the **integrity of a shipped
native binary**, or a way the library crosses a privilege boundary it should not, are very
much in scope.
