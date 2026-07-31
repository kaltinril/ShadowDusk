# Test Shader Corpus

ShadowDusk is validated against a corpus of canonical `.fx` test shaders under `tests/fixtures/shaders/`, with golden references under `tests/fixtures/golden/` — `mgfxc` `.mgfx` for `DirectX_11/`, `DirectX_12/`, `OpenGL/`, and `Vulkan/`, `fxc` fx_2_0 `.fxb` for `FNA/`, plus the `byte-identity/` manifest. The provenance of each shader and the project-owned examples are documented in the repository and reproduced here as the single source of truth:

[!INCLUDE [test-shader-corpus](../../docs/test-shader-corpus.md)]
