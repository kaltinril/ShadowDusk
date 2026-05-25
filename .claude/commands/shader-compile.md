# /shader-compile — Compile a single .fx shader file

Compiles one HLSL Effect (.fx) file to a target platform using ShadowDusk CLI, then reports the result.

## Usage
Invoke as: `/shader-compile <path-to-fx-file> [platform]`

If no platform is specified, default to `OpenGL` (the most cross-platform target).

## Steps

1. **Verify the input file exists** and has a `.fx` or `.hlsl` extension.

2. **Run the compiler:**
```bash
dotnet run --project src/ShadowDusk.Cli -- \
  --input "$INPUT_FILE" \
  --platform "$PLATFORM" \
  --output "$INPUT_FILE_WITHOUT_EXTENSION.mgfx"
```

Valid `--platform` values: `OpenGL`, `DirectX`, `Metal`

3. **On success**, report:
   - Output file path and size
   - Compilation time
   - Techniques and passes found in the effect
   - Target platform

4. **On failure**, report:
   - The exact error message from the compiler
   - Source file, line number, and column
   - The raw compiler diagnostic output
   - Suggested fix if the error pattern is recognized (e.g., undeclared identifier, wrong semantic name, unsupported feature)

## Common HLSL .fx errors to recognize:
- `error X3004: undeclared identifier` → typo or missing declaration
- `error X3000: syntax error` → malformed syntax, often a missing semicolon
- `error X4507: maximum temp register index exceeded` → shader too complex for SM 3.0 target
- `error X3501: 'main': entrypoint not found` → wrong entry point name in technique declaration
- Sampler type mismatch → `sampler2D` vs `SamplerState` (use `SamplerState` for DX10+)
