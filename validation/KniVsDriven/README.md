# validation/KniVsDriven — issue #70 VS-driven render proof in real KNI

The **KNI runtime analogue of `validation/VsDriven`.** Where `VsDriven` proves the issue #70
vertex-shader fix in real MonoGame, this renders the *same* fixtures and the *same* ShadowDusk
v10 GL bytes in **real KNI v4.2.9001 (SDL2.GL)** — the runtime squarebananas actually reported
the bug on ([issue #70](https://github.com/kaltinril/ShadowDusk/issues/70)).

## What it proves

ShadowDusk's **unchanged** `EffectCompiler` (default options -> **MGFX v10 GL**) compiles the
VS-driven fixtures `VsTransformColorTexture` **and** its legacy `: POSITION` variant; those exact
bytes are loaded into a **KNI `Effect`** on SDL2.GL, rendered via the shared `validation/Shared`
recipe, and pixel-compared **same backend (GL <-> GL), the only valid kind**, in-process against
the **mgfxc OpenGL golden**. It covers **both** issue #70 root causes:

- the **asymmetric matrix transform** (the "exploded cube": a non-identity `mul(v, M)` the old GL
  path reconstructed transposed), and
- the **legacy `: POSITION` output** (written to a dead varying instead of `gl_Position`).

Identity matrices and true-`SV_Position` fixtures are transpose-/remap-invariant, so this harness
deliberately uses a non-identity matrix and the legacy POSITION form — the shapes that actually
expose #70 (background: `plan/ISSUE-70-gl-vertex-fidelity.md`).

## Why this is honest / non-vacuous

- A **runtime-integrity guard** (`Program.cs`) asserts the loaded XNA assembly is KNI's
  (`Xna.Framework.*`), **not** MonoGame's (`MonoGame.Framework`), and aborts with exit 2 otherwise,
  so a stray MonoGame assembly can never be mislabeled as a KNI render.
- The render recipe is the **shared** `validation/Shared/*.cs` (compiled against KNI here, against
  MonoGame in `validation/VsDriven`), so any delta is the runtime, not the harness.

## How to run

KNI SDL2.GL needs a real desktop OpenGL driver (works on a normal dev machine; CI desktop-GL is a
separate driver story). The harness is **not** in `ShadowDusk.slnx`, is never packed, and opts out
of central package management so the nkast pins stay local to it.

```pwsh
dotnet run --project validation/KniVsDriven
```

Renders are written to `validation/output-vs-kni/` (**gitignored — regenerable**, never committed).
A non-zero exit or an over-tolerance pixel delta is a **regression of the issue #70 fix on the KNI
runtime**, and must be fixed before shipping a change to GL vertex-shader / matrix handling.

## Pins

- `nkast.Xna.Framework[.*]` + `nkast.Kni.Platform.SDL2.GL` at **4.2.9001.\*** (KNI v4.02 line),
  `KniPlatform=DesktopGL` + `DESKTOPGL` define, per the KNI DesktopGL template.
- ShadowDusk via `ProjectReference` to `src/ShadowDusk.Compiler` (in-process, deterministic bytes,
  identical to `validation/VsDriven`'s).
