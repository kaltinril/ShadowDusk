# shadertoy2fx — GLSL → HLSL `.fx` Mapping (Phase 46)

This document describes exactly what the `ShadowDusk.ShaderToy` converter implements: the supported
ShaderToy/GLSL subset, the type / intrinsic / operator mapping tables, and the explicit reject-list.
It is derived from the actual implementation (`src/ShadowDusk.ShaderToy/`), not an aspiration.

The converter is **out-of-band**: it references nothing in the ShadowDusk compiler pipeline. Its only
job is to emit a self-contained legacy-FX9 `.fx` text that the existing pipeline then compiles to
OpenGL / DirectX / FNA. Anything outside the subset below is a **loud `Error` diagnostic** (with the
original GLSL line/column and the offending construct) — never silently-wrong output.

---

## Multipass batch-export mode (Buffer A–D / feedback)

Beyond the single-pass `Convert`, the library accepts a ShaderToy **multi-tab export** (the ShaderToy
API JSON, `{ "ver", "info", "renderpass": [...] }`) and batch-converts it. This is **NOT** a runtime /
orchestrator / emulator: it converts each render tab with the **exact same single-pass converter
above** (no behavior change) and resolves the channel wiring; the consumer writes the actual render
loop (the way MonoGame already works). The API:

- `ShaderToyProject.Parse(json)` / `TryParse(...)` — parse the export into a typed model (passes with
  `name`/`type`/`code`/`inputs`/`outputs`; inputs carry `ctype`/`channel`/`sampler`). Built on
  `System.Text.Json`.
- `MultipassConverter.Convert(project, options) → MultipassResult` —
  - finds the single `common` pass (if any) and passes its code as `ConvertOptions.CommonSource` to
    **every** other pass (so a Common-tab helper resolves in each pass);
  - converts each `buffer` and `image` pass via the existing `ShaderToyConverter.Convert` → a per-pass
    `.fx` (per-pass success + diagnostics collected);
  - **skips `sound` and `cubemap` passes with a Warning** (out of v1 scope);
  - resolves each pass's input channels: `buffer` → (source pass name, or **self = feedback**),
    `texture` → external media `src` ("supply your own texture"), every other ctype
    (keyboard/music/musicstream/mic/webcam/volume/cubemap/video) → a Warning "unsupported channel type,
    leave unbound";
  - records sampler `wrap`/`filter` per channel;
  - records the canonical execution order: **buffers in name order (Buffer A, B, C, D) then Image**
    (Common is never rendered).
- `MultipassManifest.ToJson(result)` → the machine-readable `manifest.json` (ordered passes, each
  pass's output `.fx` + channel→source wiring + feedback flags + sampler modes).
- `MultipassManifest.ToWiringMarkdown(result)` → the human `WIRING.md` (the buffer graph + a concrete
  ~15-line MonoGame `RenderTarget2D` example tailored to the graph).

**WIRING RULE.** A `buffer` input's `id` equals some renderpass's `outputs[].id` — that pass is the
source for the channel. If the matched source pass IS the same pass, the channel is **feedback** (reads
its own previous frame; ping-pong it).

**Scope note (owner-directed).** We "accept the syntax"; the render-graph is the consumer's job. The
CLI hands the consumer the per-pass `.fx`, the `manifest.json`, and the `WIRING.md` ~15-line example
they drop into their own Draw loop (allocate a target per buffer, bind prior outputs as `iChannelN` via
`ShaderToyEffect`, run in order, ping-pong feedback, last pass to screen). The hand-wired `chain2`
two-pass example in `render-proof/` renders + asserts this end to end (Buffer A gradient → Image tint).

---

## Entry point — two conventions (G2)

The converter accepts **either** of two single-pass entry conventions; it auto-detects which by the
entry **name** the shader defines (no flag, no consumer choice):

1. **ShaderToy** — `void mainImage(out vec4 fragColor, in vec2 fragCoord)` (standard) OR the
   **Godot / GdShaders** 3-parameter form `void mainImage(in vec4 inputColor, in vec2 uv,
   out vec4 outputColor)` (see *Godot mainImage* below).
2. **Plain-GLSL fragment** — `void main()` (no parameters), as used by glslViewer / Bonzomatic /
   Shadertoy-export style single-pass image shaders.

**Detection rule.**

| `mainImage` defined? | `main` defined? | Outcome |
|---|---|---|
| yes | no | ShaderToy mode (unchanged). |
| no | yes | Plain-GLSL `main()` mode. |
| no | no | "no entry point" reject. |
| yes | yes | **ShaderToy mode + Warning** — `mainImage` is canonical; the standalone `void main()` wrapper is **dropped** (not translated/emitted), and a Warning notes it was ignored in favor of `mainImage`. |

**Both entries present (the common third-party shape).** A large share of real third-party ShaderToy
shaders ship BOTH the ShaderToy `void mainImage(out vec4, in vec2)` AND a standalone
`void main(){ mainImage(gl_FragColor, gl_FragCoord.xy); }` wrapper so the same `.glsl` also runs under
a desktop runner (glslViewer / Bonzomatic / Shadertoy-export). The converter **prefers ShaderToy mode**:
`mainImage` is the canonical shader, and our harness synthesizes its own fullscreen VS/PS that calls
`mainImage` directly, so the user `void main()` is **dropped** — it is *not* translated or emitted (its
body's `gl_FragColor`/`gl_FragCoord` write target, which only the plain-GLSL harness declares, would
otherwise dangle). A **Warning** records that the wrapper was ignored. The dropped wrapper does not
change the `mainImage` translation at all (the emitted `.fx` is byte-identical to the same shader
without the wrapper). A `main()` that does **substantive work** beyond calling `mainImage` is *still*
dropped (for a ShaderToy-derived file `mainImage` is canonical; the two are never merged). This is the
only both-entries outcome — it does not affect a shader that defines just one of the two.

### ShaderToy mode (`mainImage`)

- The signature is validated: first param must be `out vec4`, second `vec2`. Wrong return type,
  wrong arity, or wrong param types/qualifiers → reject.
- **A `mainImage` PROTOTYPE (forward declaration, `void mainImage(out vec4, in vec2);` with no body)
  is NOT counted as a definition.** The common third-party desktop-export shape declares a `mainImage`
  prototype near the top, then defines it lower down (alongside a `void main()` wrapper); only the real
  DEFINITION (non-empty body) counts. Multiple *definitions* (a concatenated multi-tab / multipass file
  with two real `mainImage` bodies) → reject with a message that names the likely cause.
- The harness PS declares a local `fragColor`, computes `fragCoord` (bottom-left Y origin), and calls
  `mainImage(fragColor, fragCoord)`, returning `fragColor` as `COLOR0`.

#### The "returning" `mainImage` form (desktop-runner variant)

Several desktop runners ship `mainImage` as `vec3 mainImage(in vec2 fragCoord)` or
`vec4 mainImage(in vec2 fragCoord)` that **RETURNS** the color (rather than writing an `out vec4`
parameter), paired with the file's own `void main(){ gl_FragColor = mainImage(gl_FragCoord.xy); }`
wrapper. This is recognized as a valid entry by its shape (`vec3`/`vec4` return, a single `in vec2`
param). The harness PS calls `mainImage(fragCoord)` and returns the result as `COLOR0`; a `vec3`
return is padded to `float4(rgb, 1.0)`. The `void main()` wrapper is dropped (same as the standard
both-entries case). Render-proven by `returning_gradient` (identical gradient + orientation as the
standard `gradient_uv`).

### Godot / GdShaders `mainImage` (the alternate 3-parameter form)

GdShaders / Godot 4's ShaderToy port uses an alternate `mainImage` signature:
`void mainImage(in vec4 inputColor, in vec2 uv, out vec4 outputColor)` (the `in` qualifiers may be
`const in` or omitted). It is recognized as a valid ShaderToy-mode entry **by its 3-parameter shape**
(param0 = `in vec4`, param1 = `in vec2`, param2 = `out vec4`); any other 3-parameter `mainImage` is a
loud, located reject. The harness wires Godot's conventions:

- **`uv`** = Godot's normalized `SCREEN_UV` ([0,1]). The harness PS sets it from `fragCoord /
  iResolution.xy` (same bottom-left-Y fragCoord the standard harness computes, so the orientation
  matches the ShaderToy/Godot reference — render-proven by `varying_gradient`'s shared screen-UV path).
- **`inputColor`** = Godot's screen-texture sample at `uv`. The harness samples **`iChannel0`** at `uv`
  (`tex2D(iChannel0, uv)`) and passes it as `inputColor`; `iChannel0` is therefore always exposed as a
  drivable channel in this mode (the consumer binds the input/screen texture to it). With no channel
  bound the sample is the default texture (opaque black by the sampler's default), matching "the
  iChannel0 sample at uv (or `float4(0,0,0,1)` if no channel)".
- **`outputColor`** = the returned fragment color (`COLOR0`).

The standard 2-arg ShaderToy `mainImage` and the plain-GLSL `main` paths are unchanged; this only adds
the 3-arg Godot recognition.

### Plain-GLSL `main()` mode

- The entry must be `void main()` with **no parameters** (`void main(...)` with params → reject;
  multiple `main` → reject).
- **Fragment output.** The output is EITHER the legacy `gl_FragColor` (a write target needing no
  declaration) OR a single top-level user-declared `out vec4 <name>;` (GLSL ES 3.00 / desktop 330,
  including the `layout(location = N) out vec4 <name>;` form). The `out vec4 <name>;` declaration is
  **consumed** (it is NOT emitted as a global or effect parameter): its name becomes the local the
  synthesized PS returns as `COLOR0`. A `main()` that has **neither** a user `out vec4` nor any
  `gl_FragColor` write has no discoverable output → loud, located reject. More than one `out vec4` is a
  reject (a single-pass shader has one color output).
- **`gl_FragCoord`.** References to `gl_FragCoord` resolve to the SAME pixel coordinate the ShaderToy
  harness computes for `fragCoord` (xy = pixel coords, bottom-left Y origin; `.z` = 0, `.w` = 1). The
  harness PS reuses the identical Y-flip (`float2(uv.x, 1 - uv.y) * iResolution.xy`), so the rendered
  orientation matches the GLSL/ShaderToy reference (render-proven by `main_gradient`, which asserts the
  same orientation as the ShaderToy `gradient_uv`).
- **How it is wired.** The fragment output (`gl_FragColor` or the user name) and `gl_FragCoord` are
  bridged as `static float4` globals so the translated `void main()` (a regular HLSL function) can
  write the output and read `gl_FragCoord`; the synthesized PS sets `gl_FragCoord`, calls `main()`, and
  returns the output global. (See `HarnessGenerator.EmitPlainGlslPixelShader`.)
- **Resolution / time uniforms.** Plain-GLSL shaders read resolution/time from a custom uniform
  (`resolution`, `u_resolution`, `iResolution`, `time`, `u_time`, …); these go through the existing
  built-in / custom-uniform / alias handling (G1/G3/G4) with no special case. An undeclared identifier
  still rejects (L1). Everything else (preprocessor, structs, arrays, traps, custom uniforms) works
  identically in both modes — only the entry/harness wrapping differs.

## ShaderToy uniforms (declared in the `.fx` only when referenced)

| GLSL uniform | HLSL global emitted |
|---|---|
| `iResolution` (vec3) | `float3 iResolution;` (always emitted — the harness PS needs it for fragCoord) |
| `iTime` (float) | `float iTime;` |
| `iTimeDelta` (float) | `float iTimeDelta;` |
| `iFrame` (int) | `int iFrame;` |
| `iFrameRate` (float) | `float iFrameRate;` |
| `iMouse` (vec4) | `float4 iMouse;` |
| `iDate` (vec4) | `float4 iDate;` |
| `iSampleRate` (float) | `float iSampleRate;` |
| `iChannelTime[4]` (float) | `float iChannelTime[4];` |
| `iChannelResolution[4]` (vec3) | `float3 iChannelResolution[4];` |
| `iChannel0..3` (sampler2D) | `texture iChannelNTexture;` + `sampler2D iChannelN = sampler_state { Texture = <iChannelNTexture>; };` |

`texture(iChannelN, uv)` → `tex2D(iChannelN, uv)`.

**Deprecated aliases (auto-mapped).** The original ShaderToy spellings are rewritten to the canonical
names at the token level before parsing, so they resolve cleanly: `iGlobalTime` → `iTime`,
`iGlobalFrame` → `iFrame`.

**Redundant built-in re-declaration (dropped).** A top-level declaration that merely re-declares a
known ShaderToy built-in uniform (e.g. `uniform float iTime;`, `uniform vec2 iResolution;`, or a
redundant `uniform sampler2D iChannel0;`) is silently dropped, not rejected: the harness already
injects that global, so the declaration is harmless. This holds **even with an initializer**
(`uniform vec3 iResolution = vec3(1920, 1080, 1);`) — the built-in is host-injected, so the source's
initializer value is irrelevant and dropped. A `uniform sampler2D` of a **non-`iChannel`** name stays a
custom sampler param (see *Custom uniforms*).

**Multi-declarator uniforms (comma list).** A `uniform <type> a, b, c;` comma list is supported: each
declarator is classified independently against the shared qualifier + type — a built-in name is dropped,
a glslViewer alias is folded, and a custom name becomes its own effect parameter (optionally with its
own default value, `uniform float a, b = 1.0;`).

## Top-level mutable globals (G1)

A top-level **non-`const`** global variable of a supported type (`float g;`, `vec2 p = vec2(0.0);`,
including a comma multi-declarator `float a, b = 1.0;`) is ACCEPTED and emitted as an HLSL
`static <type> <name> [= <init>];`. A GLSL fragment-scope global is per-invocation mutable state;
HLSL `static` globals have the matching semantics, so a helper that mutates the global before
`mainImage` reads it works as written. An optional initializer is translated like a local declaration
(intrinsic renames, matrix order, width-narrowing). A mutable global is **internal state, not a
host-driven parameter** (it is NOT added to `UsedUniforms`). A mutable global of an **unsupported
type** (`double g;` etc.) stays a loud, located reject.

## User-defined structs (G6)

A top-level `struct Name { <type> member; ... };` of supported member types (scalar / vector / matrix,
or a previously-declared struct) is ACCEPTED. HLSL struct syntax matches GLSL, so the converter emits a
matching `struct Name { ... };` (member types re-spelled to HLSL) **plus a factory function**, because
GLSL's struct constructor `Name(a, b)` has no HLSL equivalent:

```
struct Particle { float2 pos; float2x2 rot; float3 color; };
Particle make_Particle(float2 pos, float2x2 rot, float3 color)
{ Particle result; result.pos = pos; result.rot = rot; result.color = color; return result; }
```

A GLSL struct constructor `Name(a, b, ...)` is rewritten to `make_Name(a, b, ...)` (the arg count must
equal the member count, else a loud reject). Struct-typed locals, parameters, and return types are
supported. **Member access `s.field` is emitted verbatim** (never run through swizzle normalization, so
a field whose name happens to be all-`stpq` is not mangled), and its type is inferred from the struct
definition, so **a matrix-typed member still hits the matrix-multiply trap**: `s.rot * v` (a `mat2`
member) emits `mul(v, s.rot)`, exactly as a bare `mat2 * vec2` would. (`struct_basic.glsl` proves this.)

**Array struct members are SUPPORTED.** A fixed-size array member (`struct S { float w[4]; vec3 t; };`)
is accepted and emitted directly (HLSL allows a struct member array, `float w[4];`). The size resolves
through the same const-int / `#define` / const-expression path as any array suffix. The generated
`make_S(...)` factory copies the array member element-by-element (HLSL FX9/SM3 has no whole-array
assignment).

**Rejected (loud, located):** a nested / inline struct member (`struct { ... } inner;`), a combined
`struct Name { ... } var;` declaration (declare the variable separately), an empty struct, a
struct-name collision, and a struct used before it is declared (forward reference). A
Common-tab struct referenced from the Image tab is not resolved (each tab parses independently) and
falls to the unknown-type reject.

## Function overloading (same name, different signatures)

GLSL and HLSL both allow multiple functions with the **same name and different parameter signatures**.
The converter accepts same-name helper overloads (`float f(float x)` + `vec2 f(vec2 v)`) and emits ALL
of them; HLSL resolves each call by its argument types exactly as GLSL does. A forward **prototype**
(empty body) is not a definition (only the later definition is emitted). A TRUE redefinition (an
identical signature with two bodies) is still an error — but two same-name functions whose parameter
lists differ are not.

## Arrays (G7)

A **fixed-size array** is accepted in **three contexts** — global (`const`/mutable), local, and a
function **parameter** — with the size suffix `[N]` allowed **after the base type** (the GLSL-canonical
position) OR after the declarator name (at most one position carries it):

| GLSL | HLSL emitted |
|---|---|
| `const float k[3] = float[](a, b, c);` | `static const float k[3] = { a, b, c };` |
| `const float[3] k = { a, b, c };` (type-side size + brace init) | `static const float k[3] = { a, b, c };` |
| `const vec2 p[2] = vec2[2](vec2(0.), vec2(1.));` | `static const float2 p[2] = { ((float2)(0.0)), ((float2)(1.0)) };` |
| `vec2[4] c = { ... };` (local, type-side size) | `float2 c[4] = { ... };` |
| `float arr[4];` (local) | `float arr[4];` |
| `void f(inout float[9] k)` (array PARAMETER) | `void f(inout float k[9])` (size on the name) |
| `arr[i]` (indexing) | `arr[i]` (unchanged; element type inferred for traps) |

A GLSL array constructor `type[](...)` / `type[N](...)` **or** a brace initializer list `{ ... }`
(GLSL ES 3.00+ aggregate initializer) becomes an HLSL brace list `{ ... }` (valid at a
declaration-initializer site, the only place an array initializer legally appears in the subset). The
element type is inferred so an indexed element still type-checks for the traps. An array **parameter**
spells its size on the HLSL declarator name (`T name[N]`), as HLSL requires.

**Array sizes from constants/expressions are SUPPORTED.** The size in `[N]` may be an integer literal,
a `#define`d constant (the preprocessor expands it to a literal), a **`const int`** name (`const int
NUM = 41; vec2 path[NUM];`), or a **constant-integer expression** of those (`int idx[NUM_TRIANGLES *
3];`, with `+ - * / %`, parentheses, unary `-`). The expression is evaluated at convert time to a
literal HLSL array size. A `const int` is recorded in source order, so a forward reference (using a
const before it is declared) still falls to the non-constant reject.

**Rejected (loud, located):** an **unsized / runtime-sized** array (`float a[];`), an array whose
declared size does not match its constructor / brace-list element count, an array sized by a
**genuinely runtime / non-constant** expression (`float a[n];` where `n` is a runtime variable), a
size on **both** the type and the name, and an array function **return type**.

## `gl_FragCoord` built-in (G3c)

`gl_FragCoord` is a predefined built-in usable **anywhere** in a `mainImage`/`main` body. It aliases
the harness pixel coordinate as a `float4`: `.xy` = fragCoord with the existing bottom-left Y
convention, `.z` = 0, `.w` = 1. A reference resolves cleanly (no "undeclared identifier"). In ShaderToy
mode the harness publishes it as a `static float4 gl_FragCoord;` and **sets it from the same pixel
coordinate it computes for `fragCoord`** before calling `mainImage` (only when the body references it);
in plain-GLSL `void main()` mode the synthesized PS always bridges it. This handles the common
third-party shape where a `mainImage` body reads `gl_FragCoord` directly.

## Custom uniforms (consumer-driven effect parameters)

A top-level `uniform <type> <name>;` of a **non**-built-in name is now ACCEPTED and emitted as its own
HLSL global, i.e. an **effect parameter the consumer drives** each frame. This broadens the tool from
pure-ShaderToy toward general single-pass GLSL image shaders (glslViewer / KodeLife / ShaderToy-derived).
Every accepted custom uniform/sampler is reported in `ConvertResult.UsedUniforms` (the drivable-parameter
list).

| GLSL custom uniform | HLSL emitted |
|---|---|
| `uniform float/int/bool <name>;` | `float/int/bool <name>;` |
| `uniform vecN/ivecN/bvecN <name>;` | `floatN/intN/boolN <name>;` |
| `uniform matN <name>;` (mat2/3/4) | `floatNxN <name>;` |
| `uniform sampler2D <name>;` | `texture <name>Texture;` + `sampler2D <name> = sampler_state { Texture = <<name>Texture>; };` |

`texture(<name>, uv)` on a custom `sampler2D` → `tex2D(<name>, uv)`, exactly as for `iChannelN`.

**Default value / initializer (G4).** A custom `uniform` MAY carry a default value
(`uniform float uGain = 1.5;`, valid GLSL 1.20+). The initializer is translated and emitted as the
HLSL parameter's default (`float uGain = 1.5;`), so the consumer gets that value unless they override
it; the uniform is still a drivable parameter (reported in `UsedUniforms`). A **sampler** uniform
cannot carry an initializer (loud reject).

**Restrictions (still loud rejects).** A custom uniform whose type is **not** in the supported set
(`struct`, array, `sampler3D`/`samplerCube`, `double`/`uint`/`uvec`/`dvec`, non-square / explicit
`matAxB`, or any unknown type) is a loud, located reject. A top-level `out` declaration of a custom
name (anything other than the plain-GLSL `out vec4 <name>;` fragment output) is a loud reject (only
`uniform` is host-drivable). A top-level `in`/`varying`/`attribute` declaration is now **ignored**, not
rejected (see *Ignored stage I/O declarations* below). The L1 rule is unchanged: a **bare,
never-declared** identifier (e.g. ISF's `RENDERSIZE`) is still a loud reject — declare it as a top-level
`uniform` to expose it.

## Ignored stage I/O declarations + screen-coordinate varyings

A top-level `in`/`varying`/`attribute` declaration (and the `layout(location = N) in <type> <name>;`
form) is **web / desktop-export VERTEX-STAGE leftover** — the fragment-stage source of a shader that was
exported alongside its vertex stage. The converter synthesizes its own fullscreen vertex shader, so it
**IGNORES** such a declaration entirely: it is not rejected, and it is not emitted as a parameter or
global. An unreferenced ignored varying simply vanishes from the output.

**The common case — a coordinate varying used as the fullscreen UV.** A large share of these exports
declare a `vec2` varying and then read it in the fragment body as the fullscreen UV. For a fixed set of
**conventional screen-coordinate varying names** the converter aliases the reference to the harness's
**normalized screen UV** (`fragCoord / iResolution.xy`, the [0,1] coordinate with the same ShaderToy
bottom-left Y origin):

```
texCoord, vUv, vUV, v_texcoord, vTextureCoord, vTexCoord, v_coord, uv, texcoord
```

The harness publishes the screen UV as a `static float2 sd_ScreenUV;` and sets it in the PS before the
entry runs (only when one of these aliases is referenced); each aliased reference is rewritten to
`sd_ScreenUV`. This is a **documented heuristic**: the names above are the only ones aliased, and the
orientation matches the gradient oracle (render-proven by `varying_gradient`).

**Heuristic boundary (loud reject).** An ignored varying whose name is **NOT** one of the conventional
coordinate names, if it is actually **referenced** in the body, stays a loud, located **undeclared
identifier** reject — we have no per-vertex value for it and refuse to invent one. (Declare it as a
`uniform` if you want to drive it as an effect parameter.)

## OpenFL / Haxe `#pragma header` exports

OpenFL / Haxe fullscreen-filter shaders start with a `#pragma header` line (OpenFL substitutes its own
GLSL header there before compiling). `#pragma` is already stripped (G5), so `#pragma header` is dropped
like any pragma. The two OpenFL fullscreen-filter globals are mapped by their **conventional
fullscreen-filter meaning**:

| OpenFL global | Maps to |
|---|---|
| `openfl_TextureCoordv` (vec2) | the harness normalized screen UV (`fragCoord / iResolution.xy`, [0,1]) — the same `sd_ScreenUV` bridge the coordinate varyings use. |
| `openfl_TextureSize` (vec2) | the resolution `iResolution.xy`. |

A reference to either resolves cleanly (no undeclared-identifier reject); these are the only two OpenFL
globals mapped.

## libretro / RetroArch (`.slang`) VERTEX/FRAGMENT stage split

A libretro / RetroArch ".slang" source wraps **both** stages in one file, gated on `VERTEX` and
`FRAGMENT` (`#if defined(VERTEX) ... #elif defined(FRAGMENT) ... #endif`, or the `#ifdef` form); the
build system `#define`s one symbol per compilation. Our converter only needs the **fragment** stage
(the harness synthesizes the vertex shader), so when a source uses this shape AND neither symbol is
otherwise `#define`d in the file, the preprocessor **seeds `FRAGMENT` = 1 (and leaves `VERTEX`
undefined = 0)** so the fragment branch (which holds the real `mainImage`/`main`) survives preprocessing
instead of being stripped to a "no entry point" reject.

This is scoped **narrowly to the VERTEX/FRAGMENT pair**: it fires only when the file has BOTH a `VERTEX`
guard and a `FRAGMENT` guard and defines NEITHER itself, so an ordinary shader using an unrelated `#if`
is never affected.

**Common alias nicety (G3).** A declared `uniform` whose **name** is a known glslViewer / KodeLife /
Bonzomatic alias AND whose **type matches the ShaderToy built-in exactly** is folded onto that built-in,
so its references Just Work and no separate parameter is exposed. The mapped exact-type aliases are:
`u_time` / `time` / `fGlobalTime` / `iGlobalTime` → `iTime` (float); `u_frame` / `iGlobalFrame` →
`iFrame` (int). (`iGlobalTime`/`iGlobalFrame` are additionally token-rewritten by the preprocessor's
deprecated-alias pass, which handles the bare-reference spelling.) Only the **zero-risk exact-type**
alias is mapped; a type-**mismatched** glslViewer alias (`vec2 u_resolution` vs `vec3 iResolution`,
`vec2 u_mouse` vs `vec4 iMouse`) is **not** aliased — it is exposed verbatim as a custom uniform
instead, so the consumer drives it directly.

## Harness synthesized into the `.fx`

- A fullscreen-quad **vertex shader** `VSMain` taking `float4 Position : POSITION` (assumed already in
  NDC), passing it through and deriving a `[0,1]` uv (`uv = (pos.x*0.5+0.5, 0.5-pos.y*0.5)`).
- A **pixel shader** `PSMain : COLOR0` that computes `fragCoord` and calls `mainImage`.
- `technique <TechniqueName> { pass P0 { VertexShader = compile vs_3_0 VSMain(); PixelShader = compile ps_3_0 PSMain(); } }`.

### fragCoord Y orientation (the origin trap)

ShaderToy `fragCoord` has a **bottom-left** origin (y grows upward). The synthesized uv has a
**top-left** origin (uv.y = 0 at the top of the screen, the D3D convention). The PS therefore flips Y
back: `fragCoord = float2(uv.x, 1.0 - uv.y) * iResolution.xy`, so the rendered image matches the
ShaderToy reference orientation. (Documented inline in `HarnessGenerator.EmitPixelShader`.)

---

## Type mapping (trap 1)

| GLSL | HLSL | | GLSL | HLSL |
|---|---|---|---|---|
| `void` | `void` | | `ivec2/3/4` | `int2/3/4` |
| `bool` | `bool` | | `bvec2/3/4` | `bool2/3/4` |
| `int` | `int` | | `mat2` | `float2x2` |
| `float` | `float` | | `mat3` | `float3x3` |
| `vec2/3/4` | `float2/3/4` | | `mat4` | `float4x4` |
| `uint` | `int` | | `uvec2/3/4` | `int2/3/4` |

**Unsigned types are mapped to signed `int`.** The supported subset has no unsigned type, so `uint` →
`int` and `uvec2/3/4` → `int2/3/4`. ShaderToy image shaders use `uint`/`uvec` almost exclusively for
hashes / bit tricks, where the signed reinterpretation is behaviorally equivalent under the bitwise
operators we pass through. (A FNA / fx_2_0 target has no integer-bitwise instruction set at all, so a
`uint`-heavy bit-hash shader compiles on GL/DX but legitimately hits the SM3 ceiling on FNA — an
inherent fx_2_0 limit, not a converter bug.)

**Vector splat (GLSL-only, expanded):** GLSL `vecN(scalar)` splats the scalar to all N components.
HLSL has no single-scalar vector constructor, so `vecN(s)` → `((floatN)(s))`. A single **vector**
argument `vecN(vM)` likewise emits a truncating cast `((floatN)(vM))` (matches GLSL truncation).

**Single-argument matrix constructors (GLSL-only, expanded):** HLSL has no diagonal / matrix-from-matrix
constructor, so the converter expands the GLSL forms to an explicit `floatNxN(...)` grid:
- `matN(scalar s)` → the GLSL **diagonal** matrix (`s` on the diagonal, 0 elsewhere; `mat3(1.0)` is the
  identity) → `floatNxN(s,0,0, 0,s,0, 0,0,s)`. A diagonal matrix is symmetric, so this is consistent
  with the trap-2 transpose convention.
- `matN(matM m)` → the GLSL upper-left `min(N,M)` **submatrix** of `m`, with any remaining diagonal
  completed to 1 (identity completion) → a `floatNxN(...)` grid reading `m[r][c]` (the two trap-2
  transposes cancel, so the HLSL components are copied directly). `mat3(mat4)` extracts the upper-left
  3x3; `mat4(mat3)` expands the 3x3 into a 4x4 with a 1 in the bottom-right.

A single **vector** argument to a matrix constructor is not a defined GLSL form → loud reject.

## Operator mapping

Operators pass through unchanged **except** `*` when an operand is a matrix (trap 2, below):
`+ - * / %`, `== != < > <= >=`, `&& ||`, `& | ^ << >>`, ternary `?:`, assignment
`= += -= *= /= %= &= |= ^= <<= >>=`, unary `- ! + ++ --` (prefix and postfix). Float literals without a
decimal point are normalized to `x.0` so HLSL types them as float. The bitwise operators (`& | ^ << >>`
with correct C precedence: shifts below relational, then `&`, `^`, `|`) and their compound-assign forms
map straight through to HLSL (valid on `int`); `&&`/`||` stay distinct from `&`/`|`.

Four correctness rules layer on top of that pass-through:

- **Matrix compound assignment.** A `*=` whose right-hand side is a matrix is desugared the same way
  as a binary `*` (trap 2): GLSL `v *= M` (`M` a `matN`) means `v = M*v`, which under the
  `A*B → mul(B,A)` rule emits `v = mul(v, M)`. A plain `v *= M` would be invalid HLSL
  (`float2 *= float2x2`). Scalar/vector `*=` (and every other compound op) stays component-wise.
- **No double-parenthesized conditions.** A relational/equality expression used directly as an
  `if`/`while`/`do…while`/ternary condition is NOT wrapped in its own extra parentheses (the
  condition site already supplies them), so `if (a == 0.0)` is emitted rather than `if ((a == 0.0))`
  (the latter trips fxc's `-Werror,-Wparentheses-equality`).
- **Vector equality scalarized.** A vector `==`/`!=` used in a boolean context (an `if`/`while`/
  ternary condition, or under `&&`/`||`/`!`) is reduced with `all(a == b)` / `any(a != b)`, since
  HLSL `==` on vectors yields a bool-vector that is not a valid scalar condition.
- **Explicit vector truncation.** When an initializer/assignment narrows a wider vector into a
  narrower slot (e.g. a `vec4` into a `vec2`), an explicit truncating swizzle (`.xy`/`.xyz`) is
  inserted, because GLSL truncates implicitly but stricter HLSL errors (`-Werror,-Wconversion`).

### Matrix multiply order (trap 2 — the highest-risk trap)

GLSL is column-major and evaluates `M * v` as matrix·column-vector. Two facts combine:

1. Feeding the **same scalar list** to an HLSL `floatNxN(...)` constructor that a GLSL `matN(...)`
   constructor received yields the **transpose** Mᵀ (GLSL fills column-major, HLSL row-major).
2. HLSL `mul(rowVector, M)` computes the row-major product.

So the converter emits matrix constructors with the identical scalar list (producing Mᵀ) and
translates GLSL `A * B` → HLSL `mul(B, A)`. The two transposes cancel: `mul(v, Mᵀ) == M·v`. A scalar
operand of `*` is **not** a matrix multiply — it stays `*` (componentwise scale).

**Proof (mat2 rotation):** GLSL `mat2(c,-s, s,c) * v` (rotate by +θ) → emitted
`mul(v, float2x2(c,-s, s,c))` = `(c*vx + s*vy, -s*vx + c*vy)`, the same +θ rotation. Verified against
the column-major GLSL result by hand and exercised by a regenerated smoke shader that compiles on GL
and DX.

### `mod` sign (trap 3)

GLSL `mod(x,y) = x - y*floor(x/y)` (sign follows `y`); HLSL `fmod` truncates toward zero, so they
differ for negative operands. `mod(x,y)` is emitted as a call to a generated `glsl_mod` helper
(`x - y*floor(x/y)`), overloaded for `floatN` and `floatN`/`float` operand shapes. The helper block is
emitted only when `mod` was used.

## Intrinsic mapping (trap 4 — explicit table; anything else calling-but-unmapped is rejected)

**Renamed:** `mix`→`lerp`, `fract`→`frac`, `inversesqrt`→`rsqrt`, `dFdx`→`ddx`, `dFdy`→`ddy`,
`texture`/`texture2D`→`tex2D`, `textureLod`→`tex2Dlod` (uv packed into `float4(uv,0,lod)`),
`textureGrad`→`tex2Dgrad`.

**Special-cased:** `atan(y,x)`→`atan2(y,x)`, `atan(x)`→`atan(x)`; `mod`→`glsl_mod` (see trap 3);
`matrixCompMult(a,b)`→`(a * b)` (the **componentwise** matrix product: HLSL `*` on matrices is already
componentwise — only `mul` is the linear-algebra product — so this is emitted directly and **must not**
go through the matrix-order trap, G7).

**Same name (carried over):** `clamp, min, max, abs, floor, ceil, round, trunc, sign, sqrt, exp, log,
exp2, log2, pow, sin, cos, tan, asin, acos, sinh, cosh, tanh, step, smoothstep, length, distance, dot,
cross, normalize, reflect, refract, radians, degrees, saturate, fwidth` (`fwidth` is a same-named HLSL
intrinsic available in `ps_2_x`+, which the `ps_3_0` harness targets, G7).

**Rejected (loud):** `roundEven` (round-half-to-even / banker's rounding has no faithful HLSL map —
`round` and `floor(x+0.5)` are both round-half-up, so emitting either would be subtly wrong); the
mip-bias texture form `texture(s, uv, bias)` (its `tex2Dbias` mapping does not compile on the
OpenGL/DirectX SM4 targets, so it is rejected at convert time rather than emitting GL/DX-incompatible
output); `textureCube` / `texture3D` (cubemap / 3D sampling, no faithful 2D map); `getLastFrameColor`
(feedback / previous-frame read — a single image pass cannot supply it); plus `texelFetch`,
`textureProj`, `textureSize`, fine/coarse derivatives, and bit-packing/bitfield intrinsics (see the
reject-list). Each carries a message that NAMES the construct precisely. Note `random` / `readDepth` and
similar are **not GLSL built-ins** — they are user helpers the shader never defined (or a host supplies),
so they fall to the "unknown function" reject, which is correct.

## Swizzles

`.xyzw`, `.rgba` pass through; `.stpq` is normalized to `.xyzw` (HLSL has no `stpq` set).

## Precision qualifiers (trap 5)

`highp` / `mediump` / `lowp` tokens and bare `precision …;` statements are stripped. The bare
openFrameworks header token `OF_GLSL_SHADER_HEADER` (a marker openFrameworks replaces with its own
version/precision header before compiling) is also stripped as a whole-word token, so it does not
dangle as an undeclared identifier. A stray storage
or precision modifier that appears **after** the type in a copied/generated declaration
(e.g. `float const k`, `vec2 mediump uv`) is also dropped, so the emitted HLSL is a clean
`type name` — never `type modifier name`, which the stricter HLSL compilers (fxc / FNA) reject as
"modifiers must appear before type".

## Preprocessor (C-style, runs before lex/parse)

A real line-oriented C preprocessor pass handles:

- **Conditional compilation:** `#if` / `#ifdef` / `#ifndef` / `#elif` / `#else` / `#endif`, correctly
  nested. The `#if`/`#elif` expression is a C **integer constant expression**: integer literals
  (decimal and `0x…` hex, with `u`/`l` suffixes tolerated), the operators
  `! ~ * / % + - << >> < <= > >= == != & ^ | && ||`, ternary `?:`, parentheses, and
  `defined(NAME)` / `defined NAME`. Macros are expanded inside the expression first; an identifier
  that is **not** a defined macro evaluates to `0` (the standard C rule). Inactive branches are
  dropped but each source line is preserved as a blank line, so downstream diagnostics keep pointing
  at the original line.
- **Object-like macros:** `#define NAME value` — whole-word token substitution (bounded multi-pass so
  a define-of-a-define resolves).
- **Function-like macros:** `#define F(a, b) body` — argument substitution at each call site (no-arg
  `F()` and multi-token / nested-comma arguments handled; a multi-token argument is hygienically
  wrapped in parentheses to preserve call-site precedence). A function-like macro name **not** followed
  by `(` is left as a plain identifier (C rule).
- **`#undef`** is honored in source order (a use before the `#undef` sees the old value, after sees the
  redefinition).
- **Comments on directive lines** (`#define X 0 // note`, `#if A /* x */`) are stripped before the
  body/expression is taken, so they never leak into a macro body or an `#if` expression.
- **Line continuations** (`\` at end of a physical line) are folded into one logical directive.
- **libretro / RetroArch stage split:** when the source gates both stages on `VERTEX`/`FRAGMENT`
  (`#if defined(VERTEX) ... #elif defined(FRAGMENT) ... #endif`, or the `#ifdef` form) and defines
  NEITHER symbol itself, `FRAGMENT` is seeded = 1 (and `VERTEX` left = 0) so the fragment branch
  survives — see *libretro / RetroArch VERTEX/FRAGMENT stage split* above. Scoped narrowly to that pair.
- **Harmless directives ignored (G5):** `#version`, `#extension`, `#pragma` (including OpenFL's
  `#pragma header`), `#line`, and the
  glslViewer / Bonzomatic / VShaderEd channel-binding & input **metadata** directives (recognized by
  the leading `i` ShaderToy-input convention: `#iChannel0 "tex.png"`, `#iKeyboard`, `#iMouse`,
  `#iDate`, `#iuniform`, …) are silently dropped (the host binds those inputs itself), never an error.

- **Self-referential macros follow the C "blue-paint" rule.** A macro whose body references ITSELF
  (`#define N1 $N1`, `#define A A + 1`) — directly or indirectly (`#define A B` / `#define B A`) — is
  expanded EXACTLY ONCE: the macro's own name in its expansion is left as the plain identifier, NOT
  re-expanded. This is the standard C rule and turns what used to be a runaway-expansion reject into
  correct output. (A genuinely unresolvable result — e.g. a host-template `$placeholder` left behind by
  such a macro — then fails loudly downstream, which is correct.) A depth guard remains as a backstop.

**Rejected (loud, located):** the token-paste `##` and stringize `#` operators inside a macro body
(rare in shaders; rejected rather than mis-expanded), variadic macros (`...`), and `#include` (inline
the included source instead).

## Control flow & statements supported

block `{}`, local var decl + init (incl. comma lists `float a=…, b=…;` kept as siblings, not a new
scope), expression statement, compound assignment, `if/else`, `for`, `while`, `do…while`, `return`,
`break`, `continue`, `discard`, and **`switch`** (see *switch lowering* below). User-defined functions
with `in`/`out`/`inout` params, `const`
globals (emitted as `static const`), and **top-level non-`const` mutable globals** (emitted as
`static`, see *Top-level mutable globals* above) are supported. Function prototypes are accepted and
the later definition is emitted. The GLSL **comma (sequence) operator** `a, b, c` is supported at
full-expression sites (`for` headers `for (...; ...; i++, j--)`, the comma expression statement) and
emitted as the same comma operator; it is distinct from the comma SEPARATORs in argument lists and
declarators, which stay separators (G7 parser hardening). User **structs** (G6) and fixed-size
**arrays** (G7) are supported as described above.

### `switch` lowering (portable to SM3 / FNA)

A `switch (selector) { case K: ...; break; ... default: ...; }` is parsed and **lowered to an
`if` / `else if` / `else` chain** (HLSL on the SM3 / FNA fx_2_0 targets has no native `switch`). The
selector is evaluated **exactly once** into a fresh local (`sd_swN`) so a non-pure selector is not
re-evaluated per arm; each non-default arm becomes `if/else if (sd_swN == K0 || sd_swN == K1 ...)` and
the `default` arm becomes the final `else` (emitted last regardless of source position). Multiple `case`
labels stacked on one body (`case 1: case 2: ...`) become an OR'd condition. The trailing `break;` of
each arm is consumed (a `break` outside a loop is illegal HLSL); a `return ...;` inside an arm is
preserved.

**Fall-through stays a loud reject.** A non-empty `case` body with **no terminating `break`/`return`**
is real C fall-through (control falls into the next case). Lowering that to an if/else chain would
change control flow, so it is a loud, located reject (add a `break;`). A `default` label stacked
together with `case` labels on one body is also a reject (give `default` its own arm); a `discard;`
does **not** count as a clean terminator (the next case body would still run in C).

---

## Reject-list (loud `Error` + line/column + construct)

Each of the following produces a fatal diagnostic, never silently-wrong HLSL:

- **Entry points / multipass:** missing entry point (no `mainImage` and no `main`); **multiple
  `mainImage` DEFINITIONS** (a concatenated multi-tab / multipass file — a forward *prototype* does NOT
  count, and the `vec3/vec4 mainImage(in vec2)` "returning" form and the Godot 3-arg form are accepted);
  duplicate `main`; a `main` with parameters or a `main()` with no discoverable fragment output (no user
  `out vec4` and no `gl_FragColor` write); more than one `out vec4` fragment output; `mainSound`,
  `mainVR`, `mainCubemap` (Buffer A–D multipass is implied out of scope — only a single image pass is
  emitted); a 3-parameter `mainImage` that is **not** the Godot/GdShaders shape
  `(in vec4, in vec2, out vec4)`. (**Both** a `mainImage` and a `main` is NO LONGER a reject: it prefers
  ShaderToy mode and drops the `main()` wrapper with a Warning — see *Entry point* above. The Godot 3-arg
  `mainImage` IS accepted — see *Godot mainImage* above.)
- **Types:** `double`, `dvecN`, explicit `matAxB` spellings (use `mat2/3/4`), non-square matrices,
  `sampler3D` / `samplerCube`, and any unknown type name. (`uint` / `uvecN` are now **mapped to signed
  `int` / `intN`** — see the type-mapping table — not rejected.)
- **Declarations:** a **nested / inline** struct member, a struct *array* member, a combined
  `struct Name { ... } var;` declarator, an empty struct, or a struct used before declaration (a flat
  top-level `struct` of supported member types is now **accepted**, G6); an **unsized / runtime-sized**
  array (`float a[];`), an array sized by a **non-constant / macro** expression (`float a[n];`), or an
  array whose declared size mismatches its constructor / brace list (a fixed-size array `float k[N];` /
  `float[](...)` constructor / `{ ... }` brace init is now **accepted**, G7, in global / local /
  **parameter** position); an array function **return type**;
  a top-level non-`const` global of an **unsupported type** (`double g;` etc. — supported-type mutable
  globals are now **accepted** as `static`, see G1); a custom `uniform` of an **unsupported type**
  (struct/array/`sampler3D`/`samplerCube`/`double`/non-square matrix/unknown) or a **sampler with
  an initializer**; a top-level `out` declaration of a **custom** name (anything but the plain-GLSL
  `out vec4 <name>;` fragment output). A top-level `in`/`varying`/`attribute` declaration is now
  **IGNORED** (vertex-stage leftover) rather than rejected — see *Ignored stage I/O declarations* above;
  the only related reject is a **non-coordinate** ignored varying that is actually referenced (an
  undeclared-identifier reject, since its value is unknown). A redundant re-declaration of a known
  ShaderToy built-in is dropped; a custom `uniform` of a SUPPORTED type (optionally with a default
  initializer, G4) is **accepted** as an effect parameter — see *Custom uniforms* above.
- **Undeclared identifiers:** a free identifier used in an expression that is not a local/parameter, a
  `const` global, a user function, a declared custom uniform, or a predefined ShaderToy uniform is
  rejected at convert time (with line/column) rather than leaked to a downstream "use of undeclared
  identifier" compile error. This covers non-ShaderToy builtins (e.g. ISF's `RENDERSIZE`) that were
  never declared. The deprecated `iGlobalTime`/`iGlobalFrame` aliases are auto-mapped before this
  check, so they are accepted.
- **Preprocessor:** the token-paste `##` / stringize `#` operators in a macro body, variadic macros
  (`...`), and `#include` (no file resolver). (Conditional compilation and function-like macros are
  **supported**, and `#version`/`#extension`/`#pragma`/`#line` plus glslViewer/Bonzomatic `#i*` channel
  metadata directives are now silently **ignored** rather than rejected — see *Preprocessor* above.)
- **Statements/expressions:** a `switch` with true **fall-through** (a non-empty `case` body lacking a
  terminating `break`/`return`) or a `default` stacked with `case` labels on one body (a plain
  break-terminated `switch` is now **supported** and lowered to if/else — see *switch lowering* above);
  unknown function/intrinsic that is neither a user function nor in the mapping table; `roundEven` (no
  faithful HLSL map); the mip-bias `texture(s, uv, bias)` form; `texelFetch`, `textureProj`,
  `textureSize`, fine/coarse derivatives, and bit-packing/bitfield intrinsics; **`textureCube` /
  `texture3D`** (cubemap / 3D sampling has no faithful 2D `sampler2D` map); **`getLastFrameColor`**
  (reads the shader's own previous-frame output — a feedback / multipass construct a single pass cannot
  supply). (`fwidth`, `matrixCompMult`, and the single-argument matrix constructors `matN(scalar)` /
  `matN(matM)` are now **supported** — see the intrinsic / type tables.)
- **Sampler / GL stage built-ins:** a **`sampler2D` function parameter** (`vec4 f(sampler2D tex, ...)`)
  is valid HLSL but does **not** compile through the legacy-FX9 → GL/DX pipeline (a sampler cannot be a
  function argument there, the same class of limit as the mip-bias reject) → loud, named reject (inline
  the `tex2D` on the global sampler instead). The GL stage built-ins `gl_FragDepth`, `gl_FrontFacing`,
  `gl_TexCoord`, `gl_FragData` are named-rejected (no value for a 2D fullscreen pass) rather than the
  generic undeclared-identifier message. (`gl_FragCoord` IS supported.)
- **Undeclared / host-provided identifiers:** a free identifier that is not a local/parameter, a
  `const` global, a user function, a declared custom uniform, or a predefined ShaderToy uniform is a
  loud reject whose message reads "undeclared identifier 'X' (not a ShaderToy built-in or declared
  uniform) — this shader depends on a host-provided value." This covers host-specific globals
  (terminal-cursor uniforms like `iCurrentCursor`, ISF's `RENDERSIZE`, app-specific values) whose value
  we cannot invent. We do **not** auto-expose an arbitrary unknown as a uniform (that would be guessing);
  declare it as a top-level `uniform` to drive it. A host-template `$placeholder` token is likewise a
  loud, named reject (we cannot resolve a host-substituted value).

## Notes / known limits

- **FNA / SM3 ceiling.** The emitted `.fx` is `vs_3_0`/`ps_3_0`. Complex ShaderToy shaders that
  compile fine on GL/DX may legitimately exceed fx_2_0 / SM3 instruction or loop limits on FNA — an
  inherent fx_2_0 limit the pipeline already surfaces loudly, not a converter bug.
- **Oracle.** The reference for correctness is ShaderToy's own WebGL output, not `mgfxc`/`fxc`. GL is
  the closest match; DX/FNA may differ a hair due to D3D conventions.
- The converter only emits `.fx` text. A runtime helper (`ShaderToyEffect`) that drives the uniforms
  each frame and draws a fullscreen quad is a separate deliverable; it exposes `SetCustom(name, value)`
  so the consumer drives the custom uniforms reported in `UsedUniforms`.
