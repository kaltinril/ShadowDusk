#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.ShaderToy.Runtime;

namespace ShadowDusk.ShaderToy.Sample;

/// <summary>The outcome of taking one ShaderToy <c>.glsl</c> all the way to a live effect.</summary>
/// <param name="Ok">True when the effect loaded; false carries a human-readable <see cref="Error"/>.</param>
/// <param name="Effect">The wrapped, drivable effect on success; <c>null</c> otherwise.</param>
/// <param name="UsedUniforms">The ShaderToy/custom uniforms the shader actually references.</param>
/// <param name="Error">The diagnostic text to show on failure; empty on success.</param>
public sealed record CompiledShaderToy(
    bool Ok,
    ShaderToyEffect? Effect,
    IReadOnlyList<string> UsedUniforms,
    string Error);

/// <summary>
/// The heart of the capstone: at RUNTIME, with no build step and no <c>mgfxc</c>, take ShaderToy
/// GLSL all the way to a live MonoGame <see cref="Effect"/>:
/// <list type="number">
/// <item><c>ShaderToyConverter.Convert(glsl)</c> -> HLSL <c>.fx</c> text,</item>
/// <item><c>EffectCompiler.Compile(.fx, OpenGL)</c> -> <c>.mgfx</c> bytes IN MEMORY via ShadowDusk,</item>
/// <item><c>new Effect(GraphicsDevice, mgfxBytes)</c> -> a real loaded effect,</item>
/// <item>wrap it in <see cref="ShaderToyEffect"/> for the fullscreen ShaderToy pass.</item>
/// </list>
/// Convert or compile failures are returned as text (never thrown to the caller), so the host can
/// show the diagnostic on screen instead of crashing.
/// </summary>
public static class SampleCompiler
{
    private static readonly IShaderCompiler Compiler = new EffectCompiler();

    /// <summary>The directory the bundled <c>.glsl</c> files are copied to next to the binary.</summary>
    public static string ShadersDirectory => Path.Combine(AppContext.BaseDirectory, "shaders");

    /// <summary>
    /// Run the full runtime path for one bundled shader. Returns a failure result (not an exception)
    /// when the file is missing, the convert reports an error, or the in-memory compile fails.
    /// </summary>
    public static CompiledShaderToy Build(GraphicsDevice device, ShaderEntry entry)
    {
        if (device is null)
            throw new ArgumentNullException(nameof(device));
        if (entry is null)
            throw new ArgumentNullException(nameof(entry));

        string glslPath = Path.Combine(ShadersDirectory, entry.FileName);
        if (!File.Exists(glslPath))
            return Fail($"Shader source not found:\n{glslPath}");

        string glsl = File.ReadAllText(glslPath);

        // 1. ShaderToy GLSL -> HLSL .fx (no disk, no external tool).
        ConvertResult conv = ShaderToyConverter.Convert(
            glsl, new ConvertOptions { EffectName = entry.DisplayName });
        if (!conv.Success || conv.Fx is null)
            return Fail("Convert failed:\n" + FormatDiagnostics(conv.Diagnostics));

        // 2. .fx -> .mgfx IN MEMORY via the ShadowDusk product compiler (OpenGL target).
        Result<CompiledShader, ShaderError[]> compiled =
            Compiler.Compile(conv.Fx, new CompilerOptions { Target = PlatformTarget.OpenGL });
        if (!compiled.IsSuccess)
            return Fail("In-memory compile failed:\n" + FormatErrors(compiled.Error));

        // 3. .mgfx bytes -> a real MonoGame Effect, wrapped for the fullscreen ShaderToy pass.
        Effect effect;
        try
        {
            effect = new Effect(device, compiled.Value.Data);
        }
        catch (Exception ex)
        {
            return Fail($"new Effect() threw: {ex.GetType().Name}: {ex.Message}");
        }

        var helper = new ShaderToyEffect(device, effect, ownsEffect: true);
        return new CompiledShaderToy(true, helper, conv.UsedUniforms, string.Empty);
    }

    private static CompiledShaderToy Fail(string error) =>
        new(false, null, Array.Empty<string>(), error);

    private static string FormatDiagnostics(IReadOnlyList<ConvertDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
            return "(no diagnostics)";

        var lines = new List<string>(diagnostics.Count);
        foreach (ConvertDiagnostic d in diagnostics)
            lines.Add($"  {d.Severity} ({d.Line},{d.Column}): {d.Message}");
        return string.Join('\n', lines);
    }

    private static string FormatErrors(IReadOnlyList<ShaderError> errors)
    {
        if (errors.Count == 0)
            return "(no error detail)";

        var lines = new List<string>(errors.Count);
        foreach (ShaderError e in errors)
            lines.Add("  " + e.Message);
        return string.Join('\n', lines);
    }
}
