# Phase 25 — Security Hardening

**Context:** The **product is a library** (`ShadowDusk.Compiler` / `ShadowDusk.Wasm`) whose `IShaderCompiler.CompileAsync` accepts **arbitrary untrusted `.fx` source** — whenever a consumer feeds it shader text they didn't author (an in-browser fiddle, a user-content pipeline, a hosted build service). That untrusted-input surface — *not* any one website — is what changes the threat model vs. a developer compiling their own shaders locally. The KNI/Blazor fiddle sample (Phase 22/24) is **one** such consumer, not the threat model's center.

**Prerequisite phases:** None — can be worked in parallel with any phase. Should be completed before the `ShadowDusk.Wasm` path (or any service that accepts third-party `.fx`) is publicly deployed.

---

## Untrusted-input Threat Model (library API, any consumer)

A consumer hands the library `.fx` source text + optional macro definitions originating from an untrusted party. Compilation runs in-process (desktop), or in-browser WASM, or server-side. The compiler must not:
- Read files outside the shader's declared include search paths
- Be brought down by a single large or pathological shader input
- Expose host data (paths, file contents) via error messages or side channels

Note on WASM supply chain: the `ShadowDusk.Wasm` path uses `[JSImport]` to call WASM-compiled DXC and SPIRV-Cross — it does **not** use the desktop P/Invoke native loading (`SpvcLoader`, `SpvcNative`), so DLL-planting (Finding 6) does not apply. **However**, the WASM path is *not* supply-chain-free: it ships `dxcompiler.wasm` (faithful DXC→WASM, Phase 23) and `spirv-cross.wasm` as packaged static web assets. Those `.wasm` artifacts need the **same pinning + SHA-256 integrity discipline as Finding 5** (verified in `tools/restore.*` when built/restored, and ideally subresource-integrity-checked when served). Finding 5's *mechanism* applies to the WASM artifacts even though Finding 6's does not.

---

## Findings

### Finding 1 — Path Traversal in `FileSystemIncludeResolver` (High)

**Files:** `src/ShadowDusk.Core/Preprocessor/FileSystemIncludeResolver.cs:17,26`
Also triggered via: `src/ShadowDusk.Core/Preprocessor/Preprocessor.cs:65`, `src/ShadowDusk.HLSL/DxcIncludeHandler.cs:37`

**Issue:** After `Path.GetFullPath(Path.Combine(dir, includePath))` resolves `..` sequences, there is no check that the result remains within the allowed search roots. A shader containing `#include "../../../../etc/passwd"` reads and returns the file contents to the compiler.

**Fix:** After computing `candidate = Path.GetFullPath(...)`, assert containment **with a proper boundary check — a bare `string.StartsWith` is itself a vulnerability** (root `/app/shaders` would wrongly accept the sibling `/app/shaders-evil/x`). Either compare a separator-terminated root, or use `Path.GetRelativePath` and reject any result that escapes:
```csharp
// allowedRoot is already Path.GetFullPath(...)-canonicalized
var rel = Path.GetRelativePath(allowedRoot, candidate);
if (rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(rel))
    return Result.Fail(ShaderError.IncludeNotFound(...));
```
(Equivalently: ensure `allowedRoot` ends in a `DirectorySeparatorChar` before any `StartsWith`.) Use `OrdinalIgnoreCase` only on case-insensitive filesystems (Windows); Linux paths are case-sensitive, so a blanket `OrdinalIgnoreCase` is itself slightly wrong. Apply to every search path in the resolver loop. `allowedRoot` is the canonicalized directory of the input `.fx` file plus any explicitly user-supplied `-I` paths.

**Note:** `InMemoryIncludeResolver` (which XNA Fiddle will likely use for user-submitted shaders with no file system) is not affected — it only resolves from a `Dictionary<string, string>`. Fix `FileSystemIncludeResolver` so it is safe by default for any caller.

---

### Finding 2 — No File Size Limit on `#include`d Files (Medium)

**File:** `src/ShadowDusk.Core/Preprocessor/FileSystemIncludeResolver.cs:21,30`

**Issue:** `File.ReadAllText(candidate)` reads without a size check. On Linux a shader with `#include "/dev/urandom"` or a symlink to a large file OOMs the compilation worker.

**Fix:** Before reading, check `new FileInfo(candidate).Length` and reject files above a configured ceiling (suggest 10 MB, matching a reasonable root shader limit). Return an `IncludeNotFound`-style `ShaderError` with a descriptive message.

---

### Finding 3 — No Root Shader Source Size Limit (Medium)

**File:** CLI `Program.cs` (not yet implemented), and the `IShaderCompiler` call site in `ShadowDusk.Wasm`.

**Issue:** No maximum size is enforced on the submitted shader source string before it is handed to `FxPreParser` or DXC.

**Fix:** Enforce a maximum input size (10 MB suggested) at the entry point before any allocation. In the WASM path this can be a JS-layer check on the string length before calling `[JSImport]`.

---

### Finding 4 — Macro Name Validation (Low)

**File:** `src/ShadowDusk.HLSL/Dxc/DxcFlagBuilder.cs:90-94`

**Issue:** Macro names and values supplied by the user are formatted as `-D{name}` / `-D{name}={value}` strings passed to DXC's argument array. Names containing null bytes truncate in native code; names with embedded whitespace may be mis-parsed by DXC's internal argument parser.

**Fix:** Validate macro names against `^[A-Za-z_][A-Za-z0-9_]*$` and macro values against printable ASCII (no null bytes) before building the flag list. Return a `ShaderError` with a clear message on rejection.

---

### Finding 5 — Native Binary Supply Chain (CLI / Desktop only, not WASM)

**Files:** `tools/restore.ps1`, `tools/restore.sh`

**Issue:** Restore scripts copy the SPIRV-Cross native binary from the local Vulkan SDK, vcpkg, or system package manager with no version pinning and no SHA-256 integrity check. The binary loaded in-process via P/Invoke is unaudited.

**Note:** Does not affect the `ShadowDusk.Wasm` path. Affects the CLI tool and any server-side desktop deployment.

**Fix:**
- Pin a specific SPIRV-Cross release version in the restore scripts.
- Create `tools/hashes.json` with SHA-256 values per platform binary.
- After copying, verify hash before placing binary in `tools/spirv-cross/`.
- Reject and error if hash does not match.

---

### Finding 6 — DLL Planting via Bare-Name `TryLoad` Fallback (CLI / Desktop only, not WASM)

**File:** `src/ShadowDusk.GLSL/Interop/SpvcLoader.cs:31`

**Issue:** The fallback `NativeLibrary.TryLoad(GetLibFileName(), out handle)` searches `PATH` and the process working directory. An attacker who controls either can plant a trojanised native library.

**Note:** Does not affect the `ShadowDusk.Wasm` path.

**Fix:** Replace the bare-name fallback with an explicit path relative to `AppContext.BaseDirectory`. For single-file publish, probe the DOTNET bundle extraction directory via `Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR")`.

---

## Checklist

### Untrusted-input path (block public deployment of any consumer that accepts third-party `.fx`)

- [ ] 1. Add base-directory bounds check to `FileSystemIncludeResolver.Resolve()` — path traversal fix.
- [ ] 2. Add file size limit in `FileSystemIncludeResolver` before `File.ReadAllText`.
- [ ] 3. Add root shader source size limit at the WASM entry point (`ShadowDusk.Wasm`) and CLI entry point.
- [ ] 4. Validate macro names/values in `DxcFlagBuilder` — reject null bytes and non-identifier characters.

### CLI / Desktop path (fix before public release of the dotnet tool)

- [ ] 5. Pin SPIRV-Cross version in restore scripts; add `tools/hashes.json` with SHA-256 values.
- [ ] 6. Verify SHA-256 in `restore.ps1` and `restore.sh` before accepting a binary.
- [ ] 7. Replace bare-name `TryLoad` fallback in `SpvcLoader` with explicit base-directory path.

---

## Out of Scope

- Sandboxing the DXC compilation process (would require a subprocess model — significant architecture change).
- Rate limiting / quota enforcement on submissions (the *consumer's* web-application-layer concern, not the library's).
- Auditing Vortice.Dxc's own native binary supply chain (upstream dependency, out of ShadowDusk's control).

---

## Definition of Done

The library is safe to hand untrusted `.fx` from any consumer:

1. **All four untrusted-input findings (1–4) fixed and unit-tested** with adversarial inputs: a `../`-escape include is rejected (with the separator-boundary check, *not* bare `StartsWith`), an oversized include and oversized root source are rejected before allocation, and malformed macro names/values are rejected.
2. **Findings 5–6 (CLI/desktop supply chain) fixed**, and the **WASM `.wasm` artifacts** (`dxcompiler.wasm`, `spirv-cross.wasm`) are version-pinned with SHA-256 integrity verification in `tools/restore.*` (per the Finding-5 mechanism).
3. **No host data leaks through diagnostics** — a test confirms `ShaderError` messages on a failing untrusted shader contain no absolute host paths or file contents outside the submitted source.
4. A short `SECURITY.md` (or doc section) states the library's untrusted-input guarantees and the consumer's residual responsibilities (rate-limiting, sandboxing the process if they run it server-side).
