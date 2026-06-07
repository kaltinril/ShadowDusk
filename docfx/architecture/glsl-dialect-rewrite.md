# GLSL Dialect Rewrite

SPIRV-Cross emits standards-conformant GLSL, but MonoGame's OpenGL `Effect` loader expects the **MojoShader dialect** — a specific uniform-naming and fragment-output convention inherited from XNA. ShadowDusk's managed `MonoGameGlslRewriter` (in `ShadowDusk.GLSL`) bridges that gap so the `.mgfx` loads and renders exactly like `mgfxc`'s.

The exact, **as-built** uniform-naming / dialect contract this rewrite enforces is documented in the repository and reproduced here as the single source of truth:

[!INCLUDE [glsl-uniform-naming](../../docs/glsl-uniform-naming.md)]
