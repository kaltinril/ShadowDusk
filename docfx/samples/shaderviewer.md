# ShaderViewer

A desktop MonoGame sample (`samples/ShaderViewer`) that loads ShadowDusk-compiled `.mgfx` effects and renders them so you can eyeball the result. It draws a standard cat image and applies each compiled effect, letting you step through the corpus.

## What it does

- Loads a `SpriteFont` and the cat texture (`Content/cat.jpg`).
- Reads pre-compiled `.mgfx` files from a `Shaders/DirectX_11/` output directory and creates a MonoGame `Effect` from each.
- Lets you cycle through the loaded effects (including a "no effect / passthrough" baseline) and shows any load error inline, so a bad `.mgfx` is visible rather than silent.

This is a **visual sanity check** in a real MonoGame `Effect` — a proxy on the [evidence ladder](../contributing/validation.md), not the formal pixel-equivalence harness.

## Run it

```sh
cd samples/ShaderViewer
dotnet run
```

(or `./run.ps1` on Windows). It opens a 1280×720 window titled "ShadowDusk Shader Viewer". The `.mgfx` files it loads come from compiling the [test shader corpus](../contributing/test-shader-corpus.md); produce them with the [CLI](../cli/index.md) or the content sample.

## Files

| File | Role |
|---|---|
| `Program.cs` | MonoGame host entry point |
| `Game1.cs` | loads the cat + each `.mgfx` as an `Effect`; cycles and renders them |
| `Content/Content.mgcb`, `Content/Font.spritefont`, `Content/cat.jpg` | viewer content |
