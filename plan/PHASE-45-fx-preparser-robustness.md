# Phase 45 — FX pre-parser robustness (the dropped-operator bug class)

**Status:** 🔧 **In progress (2026-06-17)** — opened from GitHub **issue #106**. Item B1 (#106
itself) is **fixed and merged to this branch** (commit `bedcebf`); items B2–B9 are catalogued
below from a three-front compile audit and are **to-fix in this phase**.
**Track:** Correctness / drop-in `mgfxc` fidelity.

## Goal

Close a whole **class** of FX pre-parser defects (not just the one in issue #106): valid HLSL/FX
that the real `mgfxc` / `fxc /T fx_2_0` accept but ShadowDusk wrongly **rejects** or **mis-rewrites**.
Every fix ships with a **permanent regression fixture compiled on OpenGL + DirectX_11 + FNA** and
unit coverage, so the construct can never silently break again across our runtimes (MonoGame / KNI /
FNA), formats (MGFX v10), and profiles (OpenGL / DirectX_11 / FNA). **Zero change to the output of
any shader that already compiled** (pinned by the cross-host byte-identity gate); these fixes only
*enable* previously-failing or mis-compiled shaders.

## Why this phase exists — the shared root cause

`src/ShadowDusk.HLSL/FxPreParser.cs` is a **flat, scope-unaware token scanner**. It strips/rewrites
FX-framework syntax (techniques, passes, render states, samplers, annotations, legacy texture/sampler
forms, legacy `tex2D`) and hands the rest to DXC (RewriteToSm4) or vkd3d (PreserveSm3 / FNA). Its
heuristics match **token patterns anywhere in the stream**, with no model of declaration scope.

`src/ShadowDusk.HLSL/Lexer/FxLexer.cs` emits single-char tokens for `{ } < > ( ) ; = , / * . -` and
**silently drops** `: + [ ] & | ! ? % ^ ~`. The stripped *output* is rebuilt from the original
source text, so dropped characters never corrupt emitted code. The **only** failure mode is a flat
heuristic **pattern-matching the fragmented token stream** and acting wrongly:
- `<=` lexes as `LAngle Equals`; `a ? b : c` loses `?`/`:` and reads as three identifiers;
  `a | b` loses `|` and reads as two identifiers; `arr[i]` loses the brackets and reads as `arr i`.

Issue #106 was the first instance (a relational operator read as an FX annotation). The audit below
found that the **same flat-scan-plus-dropped-operator pattern** produces several more defects in the
sampler, render-state, legacy-texture, and return-semantic heuristics.

## Bug catalogue

| ID | Construct (valid; `mgfxc`/`fxc` accept it) | Symptom | Targets | Root cause | Likelihood | Status |
|----|---------------------------------------------|---------|---------|------------|------------|--------|
| **B1** | `return value <= 0.5f ? 0.0f : 1.0f;` (relational/ternary in a body) | `FX0001: Expected annotation type but found '='` | GL, DX, FNA | annotation heuristic matched `Ident Ident LAngle`; `<=`→`LAngle Equals` | HIGH | ✅ **Fixed** `bedcebf` (`IsAnnotationBlockStart`) — issue #106 |
| **B2** | `sampler S = sampler_state { Texture=<T>; };` used via `T.Sample(S,uv)` (modern method, not `tex2D`) | sampler decl **erased** → DXC `undeclared identifier 'S'` | GL, DX | `FxPreParser.cs` ~443-447: a `sampler_state` not in `_legacyIntrinsicSamplers` is erased | **HIGH** (the MonoGame HiDef `SpriteEffect` / modern KNI 2D shape) | ✅ **Fixed** — pre-scan `_modernMethodSamplers` (`.Sample`/`.SampleGrad`/… on a resource); such a `sampler_state` is rewritten to a passthrough `SamplerState S;` instead of erased (a genuinely-unused one still erases). Fixture `ExModernSamplerState.fx`. |
| **B3** | `ColorWriteEnable = Red \| Green \| Blue;` | dropped `\|` → `FX0008`; bare key also `int.TryParse` → `SD0011` | GL, DX, FNA | render-state value parser reads one token then demands `;` (`FxPreParser.cs` ~902-916); `RenderStateParser.cs` ~182-188 bare-key path skips `TryParseColorWriteMask` | **HIGH** (canonical D3D9/XNA color mask) | ✅ **Fixed** — pass parser accumulates consecutive identifiers for the `ColorWriteEnable*` keys (re-joined `Red\|Green\|Blue`); bare `ColorWriteEnable` now uses `TryParseColorWriteMask` (also accepts `All`). Fixture `ExColorWriteMask.fx`. |
| **B4** | `texture Tex < string Name = "diffuse"; >;` (legacy texture + FX annotation) | dropped `<` → consume stops at the inner `;`, leaks `> ;` → DXC `expected unqualified-id` | GL, DX | `ConsumeLegacyTextureDecl` swallows only to first `;` (`FxPreParser.cs` ~1499-1511) | **HIGH** (ubiquitous in FX Composer / RenderMonkey / NVIDIA sample `.fx`) | ⬜ to-fix |
| **B5** | `Texture2D Texture : register(t0);` (a texture variable *named* `Texture`) | rewritten to broken `Texture2D Texture2D register;` | GL, DX, FNA | legacy-texture rewrite guard (`prevIsTemplateClose`) only excludes the *templated* form, not name-position (`FxPreParser.cs` ~505-523) | MED-HIGH (ordinary modern naming; MonoGame stock effects do this) | ⬜ to-fix |
| **B6** | A **vertex** shader returning `: COLOR` | `: COLOR`→`: SV_Target` rewrite fires on the VS → invalid VS semantic | GL, DX | `TryMatchColorReturnSemantic` keys on `RParen : COLOR LBrace`, cannot tell VS from PS (`FxPreParser.cs` ~597-612 / ~1545) | LOW-MOD (slightly uncertain) | ⬜ to-fix |
| **B7** | `x[i] < y ? z = w : q;` (array-indexed `<` + assignment in a ternary arm) | residual #106 false positive → `FX0001` | GL, DX, FNA | `IsAnnotationBlockStart` can't distinguish `Type Name = Value` (annotation) from dropped-`?` `y z = w` | LOW-MOD | ⬜ to-fix |
| **B8** | `sampler S : register(s0) = sampler_state { ... };` (register clause before `= sampler_state`) | dispatch routes to bare path → leaks state block → DXC error | GL, DX, FNA | `isSamplerStateForm` requires `=` immediately after the name (`FxPreParser.cs` ~396-405) | LOW | ✅ **Fixed** — dispatch skips an optional `register ( … )` before the `= sampler_state` (new `OffsetAfterOptionalRegister`, shared with the brace-form detector); `ParseSamplerDecl` already consumed the clause. Fixture `ExSamplerRegisterState.fx`. |
| **B9** | `sampler2D S = sampler_state { ... } < string UIName = "x"; >;` (sampler-level annotation) | `FX0001: Expected 'Semicolon' but found '<'` | GL, DX, FNA | `ParseSamplerDecl` hard-requires `;` right after `}` (`FxPreParser.cs` ~1108) | LOW | ✅ **Fixed** — `ParseSamplerDecl` optionally consumes a trailing `< … >` annotation before the required `;`; its span is erased in every mode (incl. PreserveSm3, where the rest of the decl stays verbatim for vkd3d). Fixture `ExSamplerAnnotation.fx`. |

(Line numbers are approximate against pre-fix `FxPreParser.cs`; the fixes will pin them.)

### Confirmed clean (audit negatives — no action needed)
`#include` resolution (handled by ShadowDusk's own `Preprocessor.Flatten`, not delegated to DXC),
all preprocessor directives, arrays / array parameters / initializer lists, qualifiers
(`static const`, `row_major`, …), `cbuffer`/`tbuffer`, `register`/`packoffset`, the `>`/`>=`
family, equality / logical / bitwise operators in normal positions, function-like macros, and
multi-entry / technique / pass annotations. The deep cause is isolated to the few flat heuristics
above.

## The deeper fix vs. targeted fixes

The principled fix for the whole class is to make the FX heuristics **scope-aware** (only fire the
annotation strip / legacy rewrites at the scope they are valid in, never mid-expression). That is the
long-term direction. For this phase we apply **targeted discriminators** per bug (lower blast radius,
each pinned by tests), and note scope-awareness as the follow-up that would subsume them. B7 in
particular is a direct argument for scope-awareness: a purely local token-shape guard cannot fully
separate `< Type Name = Value >` (annotation) from `< ident ? ident = ident :` (ternary-with-assign)
once `?`/`:` are dropped.

## Definition of Done (per bug)

1. The construct compiles (or is correctly rewritten) on **every applicable target** (GL / DX / FNA),
   verified by compiling through the real CLI pipeline.
2. A **regression `.fx` fixture** exists (all-runtime SM3/fx_2_0 subset where possible) and is wired
   into the corpus compile coverage (structural census + FNA corpus + the issue-tracking regression
   test), so it is compiled on every runtime/profile on every `dotnet test`.
3. **Unit coverage** in `tests/ShadowDusk.HLSL.Tests` (both `RewriteToSm4` and `PreserveSm3` modes
   where the bug applies).
4. **No regression**: full `dotnet test ShadowDusk.slnx` green with FNA armed
   (`SHADOWDUSK_REQUIRE_VKD3D=1`), including `CrossHostByteIdentityTests` (output byte-identity is
   unchanged for the existing corpus) and `Phase41StructuralDivergenceMatrixTests`.
5. The catalogue row above is ticked with the fixing commit.

## Cross-references
- GitHub issue **#106** (the originating report).
- Branch `fix/issue-106-relational-ternary-preparser`.
- `plan/issue-106-shader-corpus-research.md` (real-shader corpus expansion that motivated the audit).
- `docs/validation-matrix.md` §6 (the regression coverage row).
- `CLAUDE.md` → "Regression testing is always run" (the pre-merge rule).
