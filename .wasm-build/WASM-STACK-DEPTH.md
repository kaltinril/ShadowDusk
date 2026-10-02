# WASM stack depth: browser modules vs desktop natives (issue #271)

Emscripten links a 64 KB stack unless told otherwise (`-sSTACK_SIZE`). The desktop natives run
on a 1 MB (Windows) or 8 MB (Linux/macOS) thread stack. A recursive compiler that is fine on
the desktop can therefore overflow in the browser. A wasm stack overflow is **not** trapped at
the overflow: the stack grows into static data and the corruption surfaces later, as
`RuntimeError: null function or function signature mismatch`, `Aborted()`, or an out-of-bounds
access. This file records the measurements behind the stack-size settings in the build recipes.

## How it was measured (2026-10-02, Windows x64, node 22)

Depth-probe shaders (pixel shader, one technique) in six shapes, at increasing depth `n`:

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
pattern). **WASM:** the captured inputs are replayed through the product shims under node
(`shadowdusk-dxc.js`, `shadowdusk-spirv-cross.js`, `shadowdusk-vkd3d.js`); success must be
byte-identical to native. A trapped instance is discarded and the next case loads a fresh one.

## Results

### Shipped modules (64 KB default stack)

RESULTS-PENDING
