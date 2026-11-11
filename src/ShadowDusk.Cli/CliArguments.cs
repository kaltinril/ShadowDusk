#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;

namespace ShadowDusk.Cli;

internal sealed record CliArguments(
    string                SourceFile,
    string                OutputFile,
    PlatformTarget        Platform,
    bool                  Debug,
    IReadOnlyList<string> IncludePaths,
    int                   MgfxVersion,
    DxbcBackend           DxbcBackend,
    CapabilityProfile?    Profile = null,
    // ShaderToy/GLSL front-end (Phase 47). InputFormat is the --input-format value (default Auto:
    // detect from extension/content); PrintUniforms gates the drivable-uniforms note so the default
    // success path keeps stderr empty for the MGCB contract.
    InputFormat           InputFormat = InputFormat.Auto,
    bool                  PrintUniforms = false,
    // mgfxc's /Defines: macros (bug-hunt 2026-07-27 M9 — previously silently dropped).
    IReadOnlyList<UserDefine>? Defines = null,
    // True when the caller named the target (/Profile: or --target-runtime); false when Platform
    // is the mgfxc-parity DirectX_11 default. Only consulted by the SD0029 advisory for `.xnb`
    // output (Phase 64 C2): the implicit default is the one case where "it compiled" and "my
    // DesktopGL game loads it" diverge silently, and the warning is never required for correct
    // output.
    bool                  TargetIsExplicit = false,
    // Two-source HLSL frontend: --vertex <file> --pixel <file> compile a vertex and a pixel shader
    // given as two files. When set, SourceFile is the vertex file (it anchors diagnostics) and the
    // only positional argument was the output file. The entry points default to `main`.
    string?               VertexFile = null,
    string?               PixelFile = null,
    string                VertexEntry = "main",
    string                PixelEntry = "main"
)
{
    /// <summary>True when the input is a vertex file plus a pixel file rather than one source.</summary>
    public bool IsStagePair => VertexFile is not null && PixelFile is not null;
}
