#nullable enable

using System.Runtime.InteropServices;
using ShadowDusk.Core;
using ShadowDusk.GLSL.Interop;

namespace ShadowDusk.GLSL;

/// <summary>
/// The default desktop <see cref="ISpirvToGlslTranspiler"/>: transpiles SPIR-V to GLSL using
/// the native SPIRV-Cross library (via P/Invoke). This is the faithful pipeline's
/// SPIR-V → GLSL leg; the result is then rewritten into MonoGame's GLSL dialect by
/// <see cref="MonoGameGlslRewriter"/>.
/// </summary>
public sealed class SpirvCrossGlslTranspiler : ISpirvToGlslTranspiler
{
    /// <summary>
    /// Creates the transpiler, ensuring the native SPIRV-Cross library is registered/loaded.
    /// </summary>
    public SpirvCrossGlslTranspiler()
    {
        SpvcLoader.Register();
    }

    /// <inheritdoc/>
    public Result<GlslSource, ShaderError> Transpile(
        ReadOnlyMemory<byte> spirvBytes,
        CancellationToken cancellationToken = default)
    {
        // Guard the byte→word reinterpretation: MemoryMarshal.Cast silently DROPS a
        // non-multiple-of-4 tail, which would hand SPIRV-Cross a truncated module.
        if (spirvBytes.Length % 4 != 0)
            return Result<GlslSource, ShaderError>.Fail(new ShaderError(
                File: "<spirv-cross>",
                Line: 0,
                Column: 0,
                Code: "SD0100",
                Message: $"SPIRV-Cross: SPIR-V byte length {spirvBytes.Length} is not a multiple of 4 — not a valid SPIR-V module"));

        var words = MemoryMarshal.Cast<byte, uint>(spirvBytes.Span);
        return Transpile(words, cancellationToken);
    }

    /// <summary>
    /// Transpiles a SPIR-V module, supplied as a span of 32-bit words, to GLSL source.
    /// </summary>
    /// <param name="spirvWords">The SPIR-V module as little-endian 32-bit words.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The transpiled <see cref="GlslSource"/>, or a <see cref="ShaderError"/> on failure.</returns>
    public Result<GlslSource, ShaderError> Transpile(
        ReadOnlySpan<uint> spirvWords,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ctx = IntPtr.Zero;
        try
        {
            if (SpvcNative.spvc_context_create(out ctx) != SpvcResult.Success)
                return Result<GlslSource, ShaderError>.Fail(
                    new ShaderError(
                        File: "<spirv-cross>",
                        Line: 0,
                        Column: 0,
                        Code: "SD0100",
                        Message: "SPIRV-Cross [context_create]: failed to create context"));

            var words = spirvWords.ToArray();

            if (SpvcNative.spvc_context_parse_spirv(ctx, words, (nuint)words.Length, out var ir)
                != SpvcResult.Success)
                return Result<GlslSource, ShaderError>.Fail(GetLastError(ctx, "parse_spirv"));

            if (SpvcNative.spvc_context_create_compiler(
                    ctx, SpvcBackend.Glsl, ir, SpvcCaptureMode.TakeOwnership, out var compiler)
                != SpvcResult.Success)
                return Result<GlslSource, ShaderError>.Fail(GetLastError(ctx, "create_compiler"));

            if (SpvcNative.spvc_compiler_create_compiler_options(compiler, out var options)
                != SpvcResult.Success)
                return Result<GlslSource, ShaderError>.Fail(GetLastError(ctx, "create_compiler_options"));

            if (SpvcNative.spvc_compiler_options_set_bool(options, SpvcCompilerOption.FlipVertexY, true)
                != SpvcResult.Success)
                return Result<GlslSource, ShaderError>.Fail(GetLastError(ctx, "set_option FlipVertexY"));

            if (SpvcNative.spvc_compiler_options_set_bool(options, SpvcCompilerOption.FixupDepthConvention, true)
                != SpvcResult.Success)
                return Result<GlslSource, ShaderError>.Fail(GetLastError(ctx, "set_option FixupDepthConvention"));

            if (SpvcNative.spvc_compiler_options_set_uint(options, SpvcCompilerOption.GlslVersion, 140)
                != SpvcResult.Success)
                return Result<GlslSource, ShaderError>.Fail(GetLastError(ctx, "set_option GlslVersion"));

            if (SpvcNative.spvc_compiler_options_set_bool(options, SpvcCompilerOption.GlslEs, false)
                != SpvcResult.Success)
                return Result<GlslSource, ShaderError>.Fail(GetLastError(ctx, "set_option GlslEs"));

            if (SpvcNative.spvc_compiler_options_set_bool(options, SpvcCompilerOption.GlslVulkanSemantics, false)
                != SpvcResult.Success)
                return Result<GlslSource, ShaderError>.Fail(GetLastError(ctx, "set_option GlslVulkanSemantics"));

            if (SpvcNative.spvc_compiler_install_compiler_options(compiler, options)
                != SpvcResult.Success)
                return Result<GlslSource, ShaderError>.Fail(GetLastError(ctx, "install_compiler_options"));

            // Always call before compile — safe no-op on texture-free shaders; crashes if skipped on textured ones.
            if (SpvcNative.spvc_compiler_build_combined_image_samplers(compiler)
                != SpvcResult.Success)
                return Result<GlslSource, ShaderError>.Fail(GetLastError(ctx, "build_combined_image_samplers"));

            if (SpvcNative.spvc_compiler_compile(compiler, out var glslPtr)
                != SpvcResult.Success)
                return Result<GlslSource, ShaderError>.Fail(GetLastError(ctx, "compile"));

            // Marshal to managed string before context_destroy frees the pointer.
            var glsl = Marshal.PtrToStringUTF8(glslPtr) ?? string.Empty;

            return Result<GlslSource, ShaderError>.Ok(new GlslSource(glsl));
        }
        catch (DllNotFoundException ex)
        {
            // Mirror Vkd3dShaderCompiler's SD0211 policy: a missing native is a clear
            // SD-coded error with the restore instruction, never a raw exception.
            return Result<GlslSource, ShaderError>.Fail(new ShaderError(
                File: "<spirv-cross>",
                Line: 0,
                Column: 0,
                Code: "SD0103",
                Message: "SPIRV-Cross native library not found. Restore it via tools/restore.ps1 " +
                         "(places tools/spirv-cross/). Underlying error: " + ex.Message));
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or BadImageFormatException)
        {
            return Result<GlslSource, ShaderError>.Fail(new ShaderError(
                File: "<spirv-cross>",
                Line: 0,
                Column: 0,
                Code: "SD0103",
                Message: "SPIRV-Cross native library could not be loaded: " + ex.Message));
        }
        finally
        {
            if (ctx != IntPtr.Zero)
                SpvcNative.spvc_context_destroy(ctx);
        }
    }

    private static ShaderError GetLastError(IntPtr ctx, string stage)
    {
        var ptr = SpvcNative.spvc_context_get_last_error_string(ctx);
        var msg = Marshal.PtrToStringUTF8(ptr) ?? "(no error string)";
        return new ShaderError(
            File: "<spirv-cross>",
            Line: 0,
            Column: 0,
            Code: "SD0100",
            Message: $"SPIRV-Cross [{stage}]: {msg}");
    }
}
