# slang-wasm — provenance & restore recipe

In-browser **HLSL → SPIR-V** compiler for the Shader Fiddle mode-2 path
(`wwwroot/shadowdusk-dxc.js`). These are the **official prebuilt** Slang
WebAssembly artifacts — no local build is required.

| File | What it is |
|---|---|
| `slang-wasm.js` | Emscripten/embind loader (ES module, `export default` async factory). |
| `slang-wasm.wasm` | The Slang compiler compiled to WebAssembly (~21 MB). |
| `slang-wasm.d.ts` | TypeScript bindings for the embind API (renamed from the release's `interface.d.ts`). |

## Version & source

- **Slang version:** `2026.10` (confirmed at runtime via `module.getVersionString()` → `"2026.10"`).
- **Source:** GitHub release asset `slang-2026.10-wasm.zip` from
  <https://github.com/shader-slang/slang/releases/tag/v2026.10>
  (direct: <https://github.com/shader-slang/slang/releases/download/v2026.10/slang-2026.10-wasm.zip>).
- The zip contains exactly `slang-wasm.js`, `slang-wasm.wasm`, and `interface.d.ts`
  (the last copied here as `slang-wasm.d.ts`).

This matches the Slang version used in the desktop spike that proved Slang's
SPIR-V flows through ShadowDusk's `SpirvReflector` unchanged.

## Re-fetch

```bash
gh release download v2026.10 --repo shader-slang/slang \
  --pattern "slang-2026.10-wasm.zip" --dir .
unzip slang-2026.10-wasm.zip            # -> slang-wasm.js, slang-wasm.wasm, interface.d.ts
mv interface.d.ts slang-wasm.d.ts
```

To bump the version, change the tag above; then re-verify with the node harness
(see this sample's README "Verify mode 2 (Slang WASM)") that the emitted SPIR-V
still reflects identically — newer Slang may change SPIR-V naming/layout.

## API used by `shadowdusk-dxc.js`

`createGlobalSession()` → `globalSession.createSession(<SPIRV target enum>)` →
`session.loadModuleFromSource(hlsl, "user", "/user.slang")` →
`module.findAndCheckEntryPoint(entry, SLANG_STAGE_FRAGMENT=5)` →
`session.createCompositeComponentType([module, entryPoint])` → `.link()` →
`linked.getEntryPointCodeBlob(0, 0)` (returns a `Uint8Array` of SPIR-V words).
The SPIRV target enum is resolved at runtime from `getCompileTargets()` (do not
hardcode). This mirrors `slang-playground`'s `compiler.ts`.
