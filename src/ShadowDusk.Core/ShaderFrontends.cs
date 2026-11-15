#nullable enable

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ShadowDusk.Core;

/// <summary>What a source-language frontend produced: the <c>.fx</c> text and its own warnings.</summary>
/// <param name="FxText">HLSL effect source, ready for the unchanged compile pipeline.</param>
/// <param name="Warnings">The frontend's non-fatal findings, located in the ORIGINAL source.</param>
public sealed record ShaderFrontendOutput(string FxText, IReadOnlyList<ShaderError> Warnings);

/// <summary>
/// A source-language frontend: shader source that is not an HLSL effect in, <c>.fx</c> text out.
/// It is a text transform ahead of the compile pipeline, which then runs unchanged on its output.
/// Frontends live in their own packages and register with <see cref="ShaderFrontends"/>, so
/// <c>ShadowDusk.Compiler</c> never references them.
/// </summary>
public interface IShaderFrontend
{
    /// <summary>The format this frontend converts, as <see cref="ShaderFormatDetector"/> reports it.</summary>
    ShaderFormat Format { get; }

    /// <summary>
    /// Converts <paramref name="source"/> to <c>.fx</c> text. Errors and warnings are the
    /// frontend's own, located in the original source and filed under
    /// <paramref name="nameHint"/> when one is given.
    /// </summary>
    /// <param name="source">The shader source text.</param>
    /// <param name="nameHint">Optional file name, for diagnostics and generated names.</param>
    Result<ShaderFrontendOutput, ShaderError[]> ToFx(string source, string? nameHint);
}

/// <summary>
/// The process-wide registry of <see cref="IShaderFrontend"/>s that
/// <c>ShaderConverter</c> routes through. A frontend package registers itself when its assembly
/// loads; <see cref="Find"/> also loads a known frontend package that is present but has not
/// been touched yet, so adding the package is the whole setup.
/// </summary>
/// <remarks>
/// An application that is trimmed, or that for another reason cannot have an assembly loaded by
/// name, registers explicitly instead (for example <c>ShaderToyFrontend.Register()</c>).
/// </remarks>
public static class ShaderFrontends
{
    // The frontend packages Find may load by name: the format they convert and their assembly.
    private static readonly (ShaderFormat Format, string AssemblyName)[] KnownPackages =
    [
        (ShaderFormat.Glsl, "ShadowDusk.ShaderToy"),
    ];

    private static readonly ConcurrentDictionary<ShaderFormat, IShaderFrontend> Registered = new();
    private static readonly ConcurrentDictionary<string, Lazy<bool>> Probed = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers <paramref name="frontend"/> for its <see cref="IShaderFrontend.Format"/>,
    /// replacing any earlier registration for that format.
    /// </summary>
    /// <exception cref="ArgumentException">The format is one ShadowDusk reads itself
    /// (<see cref="ShaderFormat.FxEffect"/>, <see cref="ShaderFormat.Slang"/>) or
    /// <see cref="ShaderFormat.Unknown"/>.</exception>
    public static void Register(IShaderFrontend frontend)
    {
        ArgumentNullException.ThrowIfNull(frontend);
        if (frontend.Format is ShaderFormat.Unknown or ShaderFormat.FxEffect or ShaderFormat.Slang)
        {
            throw new ArgumentException(
                $"A frontend cannot be registered for {frontend.Format}: .fx effects and HLSL-compatible Slang " +
                "are read by ShadowDusk.Compiler itself, and Unknown is not a format.",
                nameof(frontend));
        }

        Registered[frontend.Format] = frontend;
    }

    /// <summary>
    /// The frontend registered for <paramref name="format"/>, or null when there is none. When
    /// none is registered and a known frontend package for the format is installed, its
    /// assembly is loaded (once) so it can register itself.
    /// </summary>
    public static IShaderFrontend? Find(ShaderFormat format)
    {
        if (Registered.TryGetValue(format, out IShaderFrontend? frontend))
            return frontend;

        foreach ((ShaderFormat known, string assemblyName) in KnownPackages)
        {
            if (known != format)
                continue;

            // Lazy, so a second caller arriving while the first is still loading the assembly
            // waits for it instead of concluding the package is absent.
            _ = Probed.GetOrAdd(assemblyName, name => new Lazy<bool>(() => LoadAndInitialize(name))).Value;
        }

        return Registered.TryGetValue(format, out frontend) ? frontend : null;
    }

    // Loading the assembly is not enough: a module initializer runs only when something in the
    // module is first used, so it is run here. A package that is not installed is the normal
    // case, not an error.
    private static bool LoadAndInitialize(string assemblyName)
    {
        try
        {
            Assembly assembly = Assembly.Load(new AssemblyName(assemblyName));
            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return false;
        }
    }
}
