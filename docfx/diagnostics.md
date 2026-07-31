# Diagnostic Codes

Every diagnostic ShadowDusk emits carries a code. This page is the complete registry: look up the code you saw in your build output to find out what triggered it.

Two things worth knowing before you read the table:

- **Codes from the *underlying* compilers are passed through verbatim** and are not listed here. When DXC, `d3dcompiler_47`, or vkd3d-shader rejects your shader, you get that compiler's own words, unedited, along with the file, line, and column it reported. ShadowDusk never rewrites or summarizes a diagnostic it did not produce.
- **`SD0400`–`SD0499` are always warnings.** They flag constructs that compile successfully but are known to fail or silently misbehave at *runtime* on narrower OpenGL stacks (WebGL, ANGLE, strict Mesa drivers), where the engine's own error is a generic draw-time exception. They never reject a shader.
- **A few codes outside that range are warnings too**, marked as such in the table: `SD0007` (a ShaderToy conversion adjustment), `SD0104` (an unrecognized vertex-input semantic defaulted to TextureCoordinate, exactly as `mgfxc` defaults and warns), and `SD0214` (DirectX12 DXIL compiled unsigned on a non-Windows host). Like every warning, they never gate output.

To see everything wrong with a shader in one pass, including warnings and across several targets, call `ValidateAsync` instead of compiling. See the [In-Memory Quickstart](getting-started/in-memory-quickstart.md).

[!INCLUDE [error-codes](../docs/error-codes.md)]
