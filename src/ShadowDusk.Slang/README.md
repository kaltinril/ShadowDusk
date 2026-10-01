# ShadowDusk.Slang

Genuine Slang input for MonoGame/KNI: `import`, generics, `interface` conformances,
everything the real [Slang](https://github.com/shader-slang/slang) compiler accepts. This
package bundles `slangc` and compiles `.slang` source with `slangc -target hlsl`, then hands
that HLSL to ShadowDusk's existing, unchanged, faithful DXC pipeline (the same DXC every `.fx`
uses).

```csharp
using ShadowDusk.Core;
using ShadowDusk.Slang;

var result = await new SlangCompiler().CompileAsync(
    slangSource, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Blur.slang" });
byte[] mgfx = result.Value.Data;
```

Nothing to install: `slangc` rides inside this package for **win-x64, linux-x64, osx-x64 and
osx-arm64** and resolves from your app's own output, whether you `dotnet run` or publish
self-contained. Host floors:

- **Linux** needs a GCC 11+ `libstdc++` (Ubuntu 22.04 or later), from the upstream binary.
- **macOS** runs anywhere .NET 8 does: **10.15+ on Intel, 11+ on Apple Silicon**. The macOS
  `slangc` is built from the same upstream release tag with a lower deployment target, because
  upstream's own macOS build requires macOS 26; its output is proven byte-identical to upstream's.

On any other host, `SlangCompiler` returns `SD0620` naming the reason instead of crashing.

This is a separate, **optional** package. A consumer who does not add it pays zero size or
dependency cost. `ShadowDusk.Compiler`'s own `.slang` support (an HLSL-compatible subset, no
extra package, every host including the browser) is untouched.

## License note

The bundled `slangc` binaries are `Apache-2.0 WITH LLVM-exception` (see
`THIRD-PARTY-NOTICES.txt` in the package); ShadowDusk itself is MIT.
