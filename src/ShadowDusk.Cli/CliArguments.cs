#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.Cli;

internal sealed record CliArguments(
    string                SourceFile,
    string                OutputFile,
    PlatformTarget        Platform,
    bool                  Debug,
    IReadOnlyList<string> IncludePaths,
    int                   MgfxVersion,
    DxbcBackend           DxbcBackend
);
