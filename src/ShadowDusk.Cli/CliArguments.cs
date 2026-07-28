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
    IReadOnlyList<UserDefine>? Defines = null
);
