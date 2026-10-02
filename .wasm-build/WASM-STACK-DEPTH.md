# WASM stack depth: browser modules vs desktop natives (issue #271)

Emscripten links a 64 KB stack unless told otherwise (`-sSTACK_SIZE`). The desktop natives run
on a 1 MB (Windows) or 8 MB (Linux/macOS) thread stack. A recursive compiler that is fine on
the desktop can therefore overflow in the browser. With emscripten's default memory layout
(static data first, stack after it, growing down) a wasm stack overflow is **not** trapped at
the overflow: the stack runs into static data and the corruption surfaces later, as a hang,
`RuntimeError: null function or function signature mismatch`, `Aborted()`, an out-of-bounds
access, or (worst) a wrong compiler diagnostic. This file records the measurements behind the
stack settings in the build recipes.

## The fix

All three modules link `-sSTACK_SIZE=8MB -sGLOBAL_BASE=8388608 -Wl,--stack-first`:

* 8 MB matches the slangc module (PR #266) and the Linux/macOS desktop thread stack.
* `--stack-first` puts the stack below static data, so overflowing even 8 MB runs off
  address 0 and traps at once instead of corrupting the module (wasm-ld requires
  `GLOBAL_BASE` >= the stack size for it). Emscripten 3.1.34 only does this itself at `-O0`.

Recipes: `.wasm-build/build-dxc-wasm.ps1` (link-time only, so `-SkipHostTblgen -SkipLib`
relinks in minutes), `.wasm-build/build-spirv-cross-wasm.ps1`, and
`.github/workflows/vkd3d-wasm-build.yml`.

**Only the stack changed.** Relinking DXC from the same Stage-1 archives *without* the new
flags reproduced the shipped `dxcompiler.wasm` bit for bit (sha256 `d5f4313b...`), so the
stack flags are the only difference in the new module. The vkd3d workflow's rebuild produced a
`vkd3d-shader.js` identical to the hosted one (`cc2c9499...`); only the `.wasm` changed. The
shipped SPIRV-Cross module had been built with an unrecorded "latest" emscripten; it is now
built with the pinned 3.1.34 like the other two. Output is unchanged on every corpus gate:
`node-test-dxc-wasm.mjs` 10/10, `node-test-spirv-cross.mjs` 2/2 + negative,
`node-test-vkd3d-wasm.mjs` 87/87 byte-identical.

Residual, not fixable by `STACK_SIZE`: wasm frames also live on the JS engine's own native
stack (about 1 MB in V8), so far deeper source ends in `RangeError: Maximum call stack size
exceeded`. With the 8 MB stack that is now the first limit hit: SPIRV-Cross on an 800-branch
else-if chain (the desktop transpiles it), DXC at a 5000-term add chain (the desktop process
itself crashes at 3200, issue #306), vkd3d at a 6400-deep call chain. The shims discard the
trapped instance and `WasmShaderCompiler` reports `SD1907`.

## How it was measured (2026-10-02, Windows x64, node 22)

Depth-probe pixel shaders (one technique) in six shapes, at increasing depth `n`:

| Shape | Body |
|---|---|
| `add` | `float x = uv.x + uv.x + ...` (`n` terms) |
| `parens` | `n` nested parentheses around `uv.x` |
| `ternary` | `n` nested `(uv.y > i ? <inner> : uv.x)` |
| `ifnest` | `n` nested `if (uv.x > i) { x += 1; ...}` |
| `elseif` | an `if / else if` chain of `n` branches |
| `calls` | `f_n(x)` calls `f_{n-1}(x)` ... down to `f0` |

**Native:** each `(shape, n, target)` runs in its own process through the real desktop
`EffectCompiler` (OpenGL: DXC then SPIRV-Cross; DirectX: vkd3d), with recording decorators
capturing the exact stage inputs and outputs (the `dxc-corpus-probe` / `Vkd3dCorpusProbe`
pattern). **WASM:** the captured inputs are replayed through the product shims under node,
each stage in its own process with a 60-120 s timeout; success must be byte-identical to the
desktop. Depths 12, 25, 50, 75, 100, 200, 400, 800, 1600 (and 3200 for the 8 MB modules).

## Results

Cells: the first depth that fails (`ok` = every measured depth matched the desktop
byte-for-byte). "n/a" = the desktop itself refuses that depth, so there is nothing to match.

| Shape | Desktop (Windows) | DXC 64 KB | DXC 8 MB | SPIRV-Cross 64 KB | SPIRV-Cross 8 MB | vkd3d 64 KB | vkd3d 8 MB |
|---|---|---|---|---|---|---|---|
| `add` | ok to 1600; **process crash** at 3200 (#306); vkd3d ok to 3200 | **hang** at 100, trap from 200 | ok | ok | ok | ok | ok |
| `parens` | ok to 200; DXC refuses > 256 (`-fbracket-depth`); vkd3d ok to 3200 | trap from 25 | ok | ok | ok | ok | ok |
| `ternary` | as `parens` | trap from 25 | ok | ok | ok | ok | ok |
| `ifnest` | ok to 200; DXC refuses > 256; vkd3d ok to 800, `E5000 memory exhausted` from 1600 | trap from 75 | ok | **corrupts from ~11-15** (10 ok) | ok | trap from 200 | ok |
| `elseif` | ok to 800; DXC SPIR-V nesting error at 1600; process crash at 3200 (#306); vkd3d ok to 800 | **wrong diagnostic** (`IDxcCompiler3::Compile failed`) from 200 | ok | **corrupts from ~11-15** | ok to 400; JS `RangeError` at 800 | trap from 75 (100 passed by luck) | ok |
| `calls` | ok to 3200 | ok | ok | ok | ok | trap from 1600 | ok to 3200; JS `RangeError` from 6400 |

The SPIRV-Cross row is the one that mattered most: a dozen nested `if`s or `else if`s is an
ordinary shader, and the shipped module failed on it with a corrupted instance.

## Gates

* `tests/ShadowDusk.BrowserTests/node-test-wasm-depth.mjs` (CI, `wasm.yml` `browser-smoke`):
  `Vkd3dCorpusProbe --depth` captures the desktop ground truth for `add` x800, `parens` x200,
  `ternary` x200, `ifnest` x200, `elseif` x400, `calls` x1600; each is replayed through the
  product shims and must be byte-identical; then a trap per module must be discarded and
  reloaded. The vkd3d depth arm reports NOT RUN while the restored vkd3d module is still the
  hosted pre-#271 build.
* `tests/ShadowDusk.BrowserTests/browser-vkd3d-gate.mjs` (CI): the same trap through the real
  `WasmShaderCompiler` in Chromium must give `SD1907`, then `SD1903` until a reload, then
  manifest-identical bytes.
