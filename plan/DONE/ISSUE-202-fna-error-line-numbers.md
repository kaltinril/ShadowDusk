# ISSUE-202 — FNA error line numbers point past the end of the file

**Status:** ✅ **FIXED 2026-09-09** on `issue/202-fna-error-line-numbers`: `Vkd3dSourceLocator`
(§6) relocates every vkd3d diagnostic on both hosts; the reporter's file now reports
`(3009,29-29)`, the corpus fixtures their real lines. Full suite 2,775 per TFM green, every FNA
corpus output byte-identical before and after (153 fixtures hashed), FNA render gate re-run
(§9). Pinned by `Vkd3dSourceLocatorTests` (pure), the `Issue202` tests in
`Vkd3dShaderCompilerTests` (real vkd3d) and `FnaDiagnosticLocationTests` (the fixtures, incl.
the reporter's file under `tests/fixtures/issues/202/`).
**Reported by:** uwx, GitHub issue [#202](https://github.com/kaltinril/ShadowDusk/issues/202)
(2026-08-18), compiling Apos.Shapes' `apos-shapes.fx` with `--target-runtime fna`.
**Scope:** every diagnostic vkd3d-shader produces — the FNA `fx_2_0` path **and** the DirectX 11
`DxbcBackend.Vkd3d` path share `Vkd3dShaderCompiler`, so both drifted. DXC (OpenGL / Vulkan /
DirectX 12 / the DX11 reflection compile) and the `d3dcompiler_47` oracle honour `#line` and are
**not** affected.

> ### What this is, and what it is not
>
> - **Defect 1 (fixed here): the location is wrong.** vkd3d 1.17 counts lines it lexes from its
>   *own* intrinsic templates against the user's file, and drops skipped `#if` arms from the count.
>   ShadowDusk then blanks its `#line` directives before vkd3d sees them and maps nothing back.
>   The reporter's 3586 is line **3009** of his file.
> - **Defect 2 (not a ShadowDusk defect): the shader cannot become an FNA effect.** `E5017 SM1 cmp
>   expression of type int` is a real vkd3d 1.17 SM ≤ 3 lowering gap on a ternary whose arms are
>   comparisons — but **`fxc /T fx_2_0` rejects the same file too** (`X3506`, then `X4505` with
>   the `ps_3_0` arm forced), so there is no fxc-equivalent output to produce. The correct
>   behaviour is a loud diagnostic at the right place, which is what defect 1 delivers.
> - **The message shape in the report (`error X0000: <file>:3586:26: E5017: …`) was already
>   fixed in 0.15.0** (2026-07-27): the reporter's CLI predates it. Current releases print the
>   MSBuild-parseable `<file>(3590,26-26): error E5017: …` with real `Line`/`Column` fields.

---

## 1. The report

> When attempting to compile apos-shapes.fx with ShadowDusk targeting FNA, the error reported has
> a line number that's larger than the amount of lines in the file itself.
> ```
> > dotnet tool run ShadowDuskCLI "Content/apos-shapes.fx" "obj\shaders/apos-shapes.fxb" --target-runtime fna
> apos-shapes.fx: error X0000: Content/apos-shapes.fx:3586:26: E5017: Aborting due to not yet implemented feature: SM1 cmp expression of type int.
> ```
> (The file is 3236 lines long with no #include statements)

## 2. Reproduction

The repository's vendored `tests/fixtures/shaders/third-party/Apos.Shapes/apos-shapes.fx` is the
**523-line upstream at commit `3fb73b8`** (539 lines with the provenance header). The reporter's
file is the **current upstream** (`Source/Content/apos-shapes.fx` at `3517579`, 2026-08-02,
3235 lines) — a different, much larger shader (an `#elif SM6` arm, seven samplers, glyph
rendering, …). Both reproduce the defect class; only the current one reproduces the reporter's
numbers. Fetched verbatim and compiled with the worktree CLI (`ShadowDuskCLI` built from
`d210214`), both spellings of the target:

```
> ShadowDuskCLI Content/apos-shapes.fx obj/shaders/apos-shapes.fxb --target-runtime fna
Content/apos-shapes.fx(3590,26-26): error E5017: Aborting due to not yet implemented feature: SM1 cmp expression of type int.
> ShadowDuskCLI Content/apos-shapes.fx obj/shaders/apos-shapes.fxb /Profile:FNA
Content/apos-shapes.fx(3590,26-26): error E5017: Aborting due to not yet implemented feature: SM1 cmp expression of type int.
```

(each preceded by ~130 `vkd3d:….:fixme:hlsl_fold_constant_exprs Fold "sin" expression.` lines on
stderr — vkd3d's own debug channel, a separate pre-existing noise issue noted in §8).

3590 in a 3235-line file: the reporter's 3586 in his then-3236-line copy, four lines further
along because upstream grew by four lines between his copy and `3517579`. The vendored 539-line
fixture fails differently — `apos-shapes.fx(205,1-1): error E5017: … Instruction type
HLSL_IR_LOOP` — and its 205 is line **204** (the `for (i = 0; i < newton_steps; i++)` loop with
a runtime trip count).

## 3. Root cause 1 — where the line number comes from

### 3.1 What vkd3d is handed

`RunFna` (`CompilationPipeline.cs`) feeds vkd3d: the FX9 pre-parse in `PreserveSm3` mode
(line-preserving: technique/pass/sampler blocks are blanked in place), then
`Preprocessor.Flatten`, which prepends a **5-line prelude** —

```
// ShadowDusk platform macros — DO NOT EDIT (generated)
#define FNA 1
#define HLSL 1
#define SM3 1
#line 1 "Content/apos-shapes.fx"
```

— then `Sm3StageReservationRewriter` (line-preserving). `validation/DumpPreprocessedHlsl` confirms
3242 lines in, i.e. the user's line *n* sits at physical line *n + 5*. So the prelude explains 5
of the 581, and it is *supposed* to be cancelled by the `#line 1`. It is not, because
[`Vkd3dShaderCompiler.cs`](../../src/ShadowDusk.HLSL/Vkd3d/Vkd3dShaderCompiler.cs) (the
`LineDirectivePattern.Replace` in `CompileCore`) **blanks every `#line` directive** before the
call (vkd3d's preprocessor ignores `#line` and prints a `fixme` per directive to stderr —
`preproc.y:644`), and the comment beside it said the quiet part, before this fix:
*"ShadowDusk maps no diagnostics through them on this path."* Every vkd3d diagnostic had therefore
always been reported in flattened-text coordinates, never the author's. That alone makes a
template-header shader (`#if OPENGL … #else … #endif`) off by two or three lines:
`DeferredSprite.fx`'s `clip(…)` on line 40 reports as 43, `ForwardLighting.fx`'s on 57 as 59.

### 3.2 Two more drifts inside vkd3d itself, measured

Planting a one-line syntax error (`int __probe = ;`) at blank lines of the reporter's file and
reading back the line vkd3d reports gives the offset **as a function of position**:

| user line | vkd3d reports | delta |
|---|---|---|
| 15 | 13 | −2 |
| 63 | 47 | −16 |
| 452 | 452 | 0 |
| 645 | 693 | **+48** |
| 918 | 1030 | +112 |
| 1692 | 2028 | +336 |
| 2967 | 3548 | +581 |
| 3229 | 3826 | +597 |

**Negative drift — skipped conditional arms vanish from the count.** vkd3d's preprocessor emits no
line for a line inside a not-taken `#if`/`#elif`/`#else` arm and no line markers to compensate
(`vkd3d_shader_preprocess` output: 3187 lines for 3242 in; the file's `#if __KNIFX__ / #elif
OPENGL / #elif SM6 / #else`, `#if SM6`, `#if VULKAN`, `#if __KNIFX__` blocks skip 56 lines under
the FNA macro set `FNA;HLSL;SM3`). Multi-line `/* … */` comments collapse to one line the same way.
Directive lines themselves survive as blank lines.

**Positive drift — intrinsic templates are lexed against the user's file.** vkd3d 1.17 implements
thirteen intrinsics by formatting an HLSL *source template* and compiling it through the same
lexer (`hlsl.y` `hlsl_compile_internal_function`, callers at 3245, 3384, 3568, 3766, 3836, 3890,
4005, 4107, 4206, 4455, 4558, 4593, 4667). The lexer's line counter lives on the shared compiler
context, not on the scanner (`hlsl.l:280-283`: `{NEWLINE} { ++ctx->location.line; …}`), and the
template compile never saves or restores it, so **every call site** of one of these intrinsics
advances the user file's line counter by the template's height — uncached (two `atan2` calls on
one line: +40), per call, at the moment the parser reduces the call. Measured per intrinsic
(one call, probe on the next line):

| intrinsic | phantom lines |
|---|---|
| `atan`, `atan2` | +20 |
| `asin`, `acos` | +11 |
| `tanh`, `lit` | +7 |
| `refract` | +6 |
| `sincos`, `smoothstep`, `sinh`, `cosh`, `dst`, `faceforward`, `modf` | +4 |
| `fwidth`, `determinant` | +3 |
| every other SM3 intrinsic tested (44 of them), operators, ternaries, `%`, `if`/`else` | 0 |

The reporter's file is an SDF renderer with dozens of `atan2`/`sincos`/`smoothstep`/`asin` calls:
+632 phantom lines by line 3009, minus 56 skipped lines, plus the 5-line prelude = **+581**. The
jump from +8 to +48 between lines 633 and 645, for example, is `PathDashCut`'s two `atan2` calls
on lines 638 and 642. Nothing in the source is unusual; the drift is purely a function of how many
template intrinsics precede the diagnostic. The 350 in the report is the same arithmetic for the
older upstream file the reporter had (fewer intrinsics ahead of the failing line).

**Columns are wrong too, differently.** vkd3d reports the column in its *own* re-spaced token
stream (`if ( isGlyph ? glyphFade <= 0.0 …`, every token separated by exactly one space, leading
indentation dropped): `int __probe=;` and `    int   __probe   =   ;` both report column 15.
The reporter's column 26 is the `<=` token; in his file it is column 29.

### 3.3 The true line

Bracketing 3590 between probes (`R(3008) = 3589`, `R(3012) = 3593`) puts it on **line 3009**:

```hlsl
    if (isGlyph ? glyphFade <= 0.0 : d >= aaSize * (1.0 - aaBias)) {
        discard;
    }
```

a ternary whose two arms are comparison results, used as the `if` condition. vkd3d's SM ≤ 3
lowering turns the ternary into a `cmp` on the bool/int operands and `d3dbc.c` has no int
`cmp` — the same `E5017` class Phase 39 recorded for `clip((c < x) ? -1 : 1)`; float-typed
ternaries lower fine.

### 3.4 Which backends share the drift

Only vkd3d's. DXC honours the `#line` directives our preprocessor plants (they are left in on
that path) and has no template-lexing bug; the `d3dcompiler_47` oracle honours them too.
`Vkd3dShaderCompiler` serves both `PlatformTarget.Fna` (SM ≤ 3 → D3D bytecode) and the default
DirectX 11 DXBC backend (SM4/5 → DXBC_TPF), so a vkd3d-only DX11 rejection (the `register(vs, c0)`
E5017 class) carried the same wrong coordinates — rarely visible because DXC compiles the same
source first for reflection and catches ordinary errors with correct lines.

## 4. Root cause 2 — the construct, and what the reference compiler does

`fxc /T fx_2_0` (Windows SDK 10.0.22621 `fxc.exe`, the same D3DCompiler_47 the
`validation/FnaValidation` oracle P/Invokes) on the reporter's file:

- **As written** (no `OPENGL` define — ShadowDusk's FNA macro set is `FNA;HLSL;SM3` and never
  defines `OPENGL`): `error X3506: Only 3_x and earlier targets are supported on this compiler`
  at `apos-shapes.fx(3233,23-64)` — the `#else` arm selects `ps_4_0`. ShadowDusk gets past this
  point only because `ResolveFnaProfile` defaults an unexpanded `PS_SHADERMODEL` macro to the
  SM3 ceiling (Phase 39 policy).
- **With `/DOPENGL=1`** (forcing the `ps_3_0` arm, the only SM3 arm the file has): 20 ×
  `error X4505: maximum temp register index exceeded` (lines 487, 769-781, 1001-1005) after four
  `X3570` unroll warnings. The pixel shader needs more than the 32 temporaries `ps_3_0` has.

So **fxc cannot produce an `fx_2_0` effect from this file either.** There is no reference output
for ShadowDusk to match, and Apos.Shapes does not ship for FNA (its profile branches are KNI /
OpenGL / SM6 / DX11). The `E5017` is a real vkd3d gap on a construct fxc handles (defect 2 above),
but closing it would not make this shader compile — it would just move the failure to the temp
register ceiling. The right outcome for #202 is therefore: **fail loudly, at line 3009 column
29, with vkd3d's message verbatim.** The int/bool-ternary gap stays documented as a known vkd3d
1.17 SM ≤ 3 limitation (a managed pre-lowering is a candidate for a shader fxc *does* accept; none
is reported).

## 5. The message shape

The report's `apos-shapes.fx: error X0000: Content/apos-shapes.fx:3586:26: E5017: …` is the
pre-0.15.0 form: the colon-style vkd3d line was not parsed, collapsed into one line-less `X0000`
entry, and the CLI led with the bare file name. Bug-hunt 2026-07-27 N9 (`df78120`, shipped in
0.15.0) added `ColonDiagnosticLine` to `D3DCompilerDiagnosticReformatter`, so the current CLI prints
`Content/apos-shapes.fx(3590,26-26): error E5017: Aborting due to …` — one location, the real
`E5017` code, MSBuild-parseable, with populated `ShaderError.Line`/`Column`. `X0000` is the
registered "compiler's own text, unparsed" passthrough (`docs/error-codes.md`), used
deliberately, and no longer applies here. Nothing to change in the formatter.

## 6. Fix design

Constraints: the compiler's text stays verbatim (`Message`, `Code`, `Severity`, `RawDiagnostics`
untouched); the emitted bytes must not move (no change to the text the *real* compile receives);
desktop P/Invoke and browser `[JSImport]` hosts must stay one backend (`Vkd3dCompileContract`);
no vkd3d fork or pin bump.

**Why not `#line`.** vkd3d's preprocessor eats `#line` (fixme). Its HLSL lexer *does* accept the
GCC-style marker `# N "file"` (`hlsl.l` `<pp>[0-9]+` → `PRE_LINE`, `hlsl.y:7190`) and re-syncs on
it — measured — but the grammar allows `preproc_directive` **only between top-level
declarations** (`hlsl_prog preproc_directive`, `hlsl.y:7045`); inside a function body, a struct,
a parameter list or an initializer it is `syntax error, unexpected PRE_LINE`. The drift accumulates
*inside* function bodies, so markers can never make a body-local diagnostic exact.

**Why not a managed model of the drift.** It is computable in principle (a per-intrinsic table,
uncached per call site, in parse order, applied to vkd3d's preprocessed text), but it needs
vkd3d's own preprocessed output (macros, skipped arms, collapsed comments), its intra-line
lookahead behaviour, and a table pinned to 1.17 internals — three ways to be silently wrong.

**Chosen: let vkd3d measure its own drift (`Vkd3dSourceLocator`).** A sentinel line `@` (the
lexer's "invalid token", which yields the unique `E5000 syntax error, unexpected invalid token`
in every syntactic position — body, expression, struct, parameter list, initializer, top level)
planted before physical line *s* makes the parse abort there and report `s + drift-before-s`
exactly as the real diagnostic on line *s* would, with macros, skipped arms, comments and
templates all accounted for by the real compiler. Bisect *s* over the flattened text until
`R(s) ≤ L < R(s+1)`: that *s* is the diagnostic's physical line. A sentinel swallowed by a block
comment or a skipped arm (the probe returns the original diagnostic, or success) is classified
"unknown" and the search steps forward with a doubling stride; a line following a `\`
continuation is never probed. Each probe is a parse-abort compile of the same request:
**12 probes cost 0.2 s against 3.3 s for the failing compile itself**, and they run only when a
located diagnostic exists (failure primary, or success warnings). The physical line then maps
through the `#line` directives of the *un*-blanked text to `(file, line)` — includes included —
and the column maps by token index: vkd3d's column identifies the *k*-th token of its re-spaced
line, and the *k*-th token of the author's line has a known source column (falls back to vkd3d's
column when the tokens do not align, e.g. a macro-expanded line). Probe results are memoised;
a budget of 128 probes leaves the diagnostic as vkd3d reported it rather than guessing.

Both hosts call the same locator with their own probe delegate (native call / JS call), so the
browser's FNA and DX exports relocate identically.

## 7. Validation gap — why nothing caught a 581-line drift

- Every FNA test asserts on *what* the diagnostic says and *which file* it names (e.g.
  `IntTernaryClip_Fna_FailsLoudlyOnVkd3dGap`: file name + non-empty message), never *where*.
  `Vkd3dCompileContractTests` pins the parser of vkd3d's text, whose input lines it invents.
- The rung-4 gates are render gates: a shader that reaches them compiles, so they cannot see a
  diagnostic's coordinates at all.
- The corpus fixtures are short template shaders whose drift is +2 or +3 (prelude minus one
  skipped arm): small enough to read as "roughly right" if anyone looked, and nobody asserted.
- The only shader in the corpus with dozens of template intrinsics ahead of a failure is the
  vendored Apos.Shapes, classified "reject on FNA" with no assertion on the location.

Lesson, recorded in `project_rules.md`: **a diagnostic test that does not assert the line and
column is not testing the location contract.** The new tests plant a known construct at a known
line behind known drift and assert the exact `(File, Line, Column)`.

## 8. Related observations (not fixed here)

- `vkd3d:…:fixme:hlsl_fold_constant_exprs` lines reach the CLI's stderr on a failing compile
  despite `Vkd3dLoader` defaulting `VKD3D_DEBUG=none` in-process: the MinGW CRT inside the DLL
  reads its own environment copy, so `Environment.SetEnvironmentVariable` does not reach
  `getenv`. Pre-existing; the code comment already notes it does not reliably reach the native.
- The vendored 539-line fixture fails on a different vkd3d 1.17 gap (`HLSL_IR_LOOP`: a `for`
  whose trip count is a runtime `int`), so the `NOTICE.md` rationale ("exceeds the SM3 ceiling")
  is corrected to name the real construct and code.

## 9. Acceptance

- `ShadowDuskCLI Content/apos-shapes.fx out.fxb --target-runtime fna` on the reporter's file
  reports `Content/apos-shapes.fx(3009,29-29): error E5017: Aborting due to not yet implemented
  feature: SM1 cmp expression of type int.` — the `if (isGlyph ? …)` line — and the two small
  corpus fixtures report their `clip(…)` lines (40 and 57), the vendored fixture its `for` (204).
- Pure unit tests pin the locator against a fake vkd3d (drift, skipped arms, comments, syntax-error
  originals, `#line` includes, column remap, budget); a `[Vkd3dFact]` test pins it against the
  real vkd3d with a known construct at a known line behind known drift; the FNA integration tests
  assert exact lines, including the reporter's file vendored as `apos-shapes-issue-202.fx`.
- Emitted bytes unchanged on every target (the probes never touch the real compile's input).
