#nullable enable

using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.Integration.Tests.MgcbPlugin;

/// <summary>
/// The minimum <see cref="ContentProcessorContext"/> a content processor needs, so
/// <c>ShadowDuskEffectProcessor.Process</c> can be driven under <c>dotnet test</c> without
/// spawning a real MGCB build. Only the members the processor actually reads are meaningful
/// (<see cref="TargetPlatform"/>, <see cref="BuildConfiguration"/>, <see cref="Logger"/>) plus
/// <c>AddDependency</c>, which is recorded so a test can assert include tracking. Everything
/// else throws: a processor that starts calling <c>BuildAsset</c>/<c>Convert</c> would be doing
/// something this plugin has no business doing, and a silent stub would hide it.
/// </summary>
internal sealed class FakeContentProcessorContext : ContentProcessorContext
{
    private readonly List<string> _dependencies = [];
    private readonly List<string> _outputFiles = [];

    public FakeContentProcessorContext(
        TargetPlatform targetPlatform,
        string buildConfiguration = "",
        string intermediateDirectory = "",
        string outputDirectory = "",
        string outputFilename = "")
    {
        TargetPlatform        = targetPlatform;
        BuildConfiguration    = buildConfiguration;
        IntermediateDirectory = intermediateDirectory;
        OutputDirectory       = outputDirectory;
        OutputFilename        = outputFilename;
        Logger                = new RecordingBuildLogger();
    }

    public IReadOnlyList<string> Dependencies => _dependencies;

    public IReadOnlyList<string> OutputFiles => _outputFiles;

    public RecordingBuildLogger RecordingLogger => (RecordingBuildLogger)Logger;

    public override string BuildConfiguration { get; }

    public override string IntermediateDirectory { get; }

    public override ContentBuildLogger Logger { get; }

    public override ContentIdentity SourceIdentity { get; } = new();

    public override string OutputDirectory { get; }

    public override string OutputFilename { get; }

    public override OpaqueDataDictionary Parameters { get; } = new();

    public override TargetPlatform TargetPlatform { get; }

    public override GraphicsProfile TargetProfile { get; } = GraphicsProfile.Reach;

    public override void AddDependency(string filename) => _dependencies.Add(filename);

    public override void AddOutputFile(string filename) => _outputFiles.Add(filename);

    public override TOutput Convert<TInput, TOutput>(
        TInput input, string processorName, OpaqueDataDictionary processorParameters)
        => throw new NotSupportedException(
            "ShadowDuskEffectProcessor must not build or convert nested assets.");

    public override TOutput BuildAndLoadAsset<TInput, TOutput>(
        ExternalReference<TInput> sourceAsset,
        string processorName,
        OpaqueDataDictionary processorParameters,
        string importerName)
        => throw new NotSupportedException(
            "ShadowDuskEffectProcessor must not build or convert nested assets.");

    public override ExternalReference<TOutput> BuildAsset<TInput, TOutput>(
        ExternalReference<TInput> sourceAsset,
        string processorName,
        OpaqueDataDictionary processorParameters,
        string importerName,
        string assetName)
        => throw new NotSupportedException(
            "ShadowDuskEffectProcessor must not build or convert nested assets.");
}

/// <summary>A <see cref="ContentBuildLogger"/> that records what the processor logged.</summary>
internal sealed class RecordingBuildLogger : ContentBuildLogger
{
    private readonly List<string> _messages = [];
    private readonly List<string> _warnings = [];

    public IReadOnlyList<string> Messages => _messages;

    public IReadOnlyList<string> Warnings => _warnings;

    public override void LogMessage(string message, params object[] messageArgs)
        => _messages.Add(Format(message, messageArgs));

    public override void LogImportantMessage(string message, params object[] messageArgs)
        => _messages.Add(Format(message, messageArgs));

    public override void LogWarning(
        string helpLink, ContentIdentity contentIdentity, string message, params object[] messageArgs)
        => _warnings.Add(Format(message, messageArgs));

    // Mirrors what MonoGame's own logger does, so a message carrying an unescaped '{' (HLSL is
    // full of braces) blows up here exactly as it would in a real build.
    private static string Format(string message, object[] messageArgs)
        => messageArgs.Length == 0 ? message : string.Format(message, messageArgs);
}
