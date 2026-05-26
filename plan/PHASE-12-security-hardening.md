# Phase 12 — Security Hardening

**Context:** ShadowDusk processes user-submitted `.fx` shader files in the XNA Fiddle web context (https://xnafiddle.net/). Arbitrary untrusted input changes the threat model vs. a local developer CLI tool.

**Prerequisite phases:** None — can be worked in parallel with any phase. Should be completed before the `ShadowDusk.Wasm` path is publicly deployed.

---

## Web / WASM Threat Model

Users submit `.fx` source text and optional macro definitions via the XNA Fiddle UI. The compilation runs server-side or in-browser WASM. The compiler must not:
- Read files outside the shader's declared include search paths
- Be brought down by a single large or pathological shader input
- Expose server-side data via error messages or side channels

Note: the `ShadowDusk.Wasm` path uses `[JSImport]` to call WASM-compiled DXC and SPIRV-Cross — it does **not** use the P/Invoke native library loading (`SpvcLoader`, `SpvcNative`). DLL planting and supply-chain findings from the Phase 1–6 security review do not apply to the WASM path.

---

## Findings

### Finding 1 — Path Traversal in `FileSystemIncludeResolver` (High)

**Files:** `src/ShadowDusk.Core/Preprocessor/FileSystemIncludeResolver.cs:17,26`
Also triggered via: `src/ShadowDusk.Core/Preprocessor/Preprocessor.cs:65`, `src/ShadowDusk.HLSL/DxcIncludeHandler.cs:37`

**Issue:** After `Path.GetFullPath(Path.Combine(dir, includePath))` resolves `..` sequences, there is no check that the result remains within the allowed search roots. A shader containing `#include "../../../../etc/passwd"` reads and returns the file contents to the compiler.

**Fix:** After computing `candidate = Path.GetFullPath(...)`, assert:
```csharp
if (!candidate.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
    return Result.Fail(ShaderError.IncludeNotFound(...));
```
Apply to every search path in the resolver loop. `allowedRoot` is the canonicalized directory of the input `.fx` file plus any explicitly user-supplied `-I` paths.

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

### Web / WASM path (block deployment of XNA Fiddle integration)

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
- Rate limiting / quota enforcement on XNA Fiddle submissions (web application layer concern, not ShadowDusk).
- Auditing Vortice.Dxc's own native binary supply chain (upstream dependency, out of ShadowDusk's control).
