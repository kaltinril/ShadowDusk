#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.GLSL;
using ShadowDusk.HLSL.Dxc;

namespace ShadowDusk.Probes;

/// <summary>
/// Issue #304 follow-up: the OpenGL corpus compiled through the REAL pipeline, recording what
/// each native produced on the way (every SPIR-V module DXC returned, every GLSL text
/// SPIRV-Cross returned) next to the final <c>.mgfx</c>. One source file, compiled into both the
/// desktop test (<c>OpenGlIntermediatesByteIdentityTests</c>, which pins the committed
/// <c>intermediates-manifest.json</c>) and the Android harness (<c>validation/AndroidGl</c>, which
/// checks the same corpus ON the device against that manifest), so the two sides cannot hash
/// differently.
///
/// <para>The recorders only observe: they wrap the default <see cref="DxcShaderCompiler"/> and
/// <see cref="SpirvCrossGlslTranspiler"/> (exactly what <c>new EffectCompiler()</c> builds) and
/// return their results untouched. Both sides use the managed <see cref="SpirvReflector"/>, the
/// one Android selects by default, so the DXC requests are the same on both.</para>
/// </summary>
public static class OpenGlCorpusProbe
{
    /// <summary>The prefix of the OpenGL entries in <c>manifest.json</c>.</summary>
    public const string Prefix = "OpenGL/";

    /// <summary>The hashes of one compile.</summary>
    /// <param name="Mgfx">SHA-256 of the <c>.mgfx</c> bytes.</param>
    /// <param name="Spirv">SHA-256 over the sorted SHA-256s of every SPIR-V module DXC returned.</param>
    /// <param name="Glsl">SHA-256 over the sorted SHA-256s of every GLSL text SPIRV-Cross returned.</param>
    /// <param name="SpirvCount">How many SPIR-V modules were recorded.</param>
    /// <param name="GlslCount">How many GLSL texts were recorded.</param>
    public sealed record Hashes(string Mgfx, string Spirv, string Glsl, int SpirvCount, int GlslCount);

    /// <summary>The OpenGL fixture names (relative to <c>tests/fixtures/shaders</c>) in <paramref name="manifestJson"/>.</summary>
    public static IReadOnlyList<string> OpenGlFixtures(string manifestJson) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(manifestJson)!
            .Keys.Where(k => k.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(k => k.Substring(Prefix.Length))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

    /// <summary>Compiles <paramref name="source"/> for OpenGL and hashes the output and both intermediates.</summary>
    public static async Task<Result<Hashes, string>> CompileAsync(string fixture, string source, CancellationToken ct)
    {
        var spirv = new ConcurrentBag<string>();
        var glsl = new ConcurrentBag<string>();
        var compiler = new EffectCompiler(
            dxcCompilerFactory: () => new RecordingDxc(new DxcShaderCompiler(), spirv),
            glslTranspilerFactory: () => new RecordingTranspiler(new SpirvCrossGlslTranspiler(), glsl),
            reflectorFactory: () => new SpirvReflector());

        var options = new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            // The fixed fixture-relative name, as CrossHostByteIdentityTests uses.
            SourceFileName = fixture,
        };

        // Line endings normalized, as CrossHostByteIdentityTests does: EOL is the checkout's doing.
        var result = await compiler.CompileAsync(source.Replace("\r\n", "\n", StringComparison.Ordinal), options, ct)
            .ConfigureAwait(false);
        if (result.IsFailure)
            return Result<Hashes, string>.Fail(string.Join(" | ", result.Error.Select(e => e.Code + ": " + e.Message)));

        return Result<Hashes, string>.Ok(new Hashes(
            Hex(SHA256.HashData(result.Value.Data)), Digest(spirv), Digest(glsl), spirv.Count, glsl.Count));
    }

    /// <summary>The manifest JSON for <paramref name="hashes"/>, keyed <c>OpenGL/&lt;fixture&gt;</c>.</summary>
    public static string ToManifestJson(IReadOnlyDictionary<string, Hashes> hashes)
    {
        var sorted = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        foreach ((string fixture, Hashes h) in hashes)
        {
            sorted[Prefix + fixture] = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["glsl"] = h.Glsl,
                ["spirv"] = h.Spirv,
            };
        }

        return JsonSerializer.Serialize(sorted, new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
    }

    /// <summary>Reads <c>intermediates-manifest.json</c>: key -> (spirv, glsl).</summary>
    public static IReadOnlyDictionary<string, (string Spirv, string Glsl)> ReadIntermediates(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json)!
            .ToDictionary(kv => kv.Key, kv => (kv.Value["spirv"], kv.Value["glsl"]), StringComparer.Ordinal);

    private static string Digest(IEnumerable<string> hashes) =>
        Hex(SHA256.HashData(Encoding.ASCII.GetBytes(string.Join("\n", hashes.OrderBy(h => h, StringComparer.Ordinal)))));

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private sealed class RecordingDxc(DxcShaderCompiler inner, ConcurrentBag<string> spirv) : IDxcShaderCompiler, IDisposable
    {
        public async Task<Result<PlatformBlob, ShaderError>> CompileAsync(DxcCompileRequest request, CancellationToken cancellationToken = default) =>
            Record(await inner.CompileAsync(request, cancellationToken).ConfigureAwait(false));

        public Result<PlatformBlob, ShaderError> Compile(DxcCompileRequest request, CancellationToken cancellationToken = default) =>
            Record(inner.Compile(request, cancellationToken));

        public Result<string, ShaderError> Preprocess(DxcPreprocessRequest request, CancellationToken cancellationToken = default) =>
            inner.Preprocess(request, cancellationToken);

        public void Dispose() => inner.Dispose();

        private Result<PlatformBlob, ShaderError> Record(Result<PlatformBlob, ShaderError> result)
        {
            if (result.IsSuccess && result.Value.Kind == BlobKind.Spirv)
                spirv.Add(Hex(SHA256.HashData(result.Value.Bytes.Span)));
            return result;
        }
    }

    private sealed class RecordingTranspiler(SpirvCrossGlslTranspiler inner, ConcurrentBag<string> glsl) : ISpirvToGlslTranspiler
    {
        public Result<GlslSource, ShaderError> Transpile(ReadOnlyMemory<byte> spirvBytes, CancellationToken cancellationToken = default)
        {
            var result = inner.Transpile(spirvBytes, cancellationToken);
            if (result.IsSuccess)
                glsl.Add(Hex(SHA256.HashData(Encoding.UTF8.GetBytes(result.Value.Text))));
            return result;
        }
    }
}
