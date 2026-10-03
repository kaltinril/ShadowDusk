#nullable enable

// DepthProbe — the DESKTOP ground-truth half of the WASM stack-depth gate (issue #271,
// node-test-wasm-depth.mjs). Emscripten links a 64 KB stack by default where the desktop
// natives get 1 MB (Windows) or 8 MB (Linux/macOS), so deeply nested but valid shaders could
// trap, hang or miscompile in the browser while the desktop compiled them. The browser
// modules now link an 8 MB stack; this probe captures what the desktop produces for nested
// shapes the 64 KB modules failed on, and the node gate replays them through the product
// shims and requires byte-identical output.
//
// For each case it drives the REAL pipeline (EffectCompiler) twice: OpenGL with a recording
// DXC decorator (the exact preprocessed HLSL + DXC argument list + SPIR-V, then the desktop
// SpirvCrossGlslTranspiler's GLSL from that SPIR-V) and DirectX with a recording vkd3d
// decorator (source, entry point, profile, target type, compile options, DXBC).
//
// Usage: dotnet run --project Vkd3dCorpusProbe -- --depth <repoRoot> <outDir>

using System.Text;
using System.Text.Json;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.GLSL;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Vkd3d;

internal static class DepthProbe
{
    /// <summary>The nested shapes and depths. Every one compiles on the desktop (both hosts'
    /// thread stacks) and failed in the 64 KB-stack browser modules
    /// (.wasm-build/WASM-STACK-DEPTH.md). Kept below DXC's 256 bracket-nesting limit where the
    /// shape is bracketed.</summary>
    internal static readonly (string Kind, int Depth)[] Cases =
    [
        ("add", 800),      // 64 KB DXC: hung at 100 terms, trapped from 200
        ("parens", 200),   // 64 KB DXC: trapped from 25
        ("ternary", 200),  // 64 KB DXC: trapped from 25
        ("ifnest", 200),   // 64 KB SPIRV-Cross: corrupted/trapped from ~15; DXC from 75; vkd3d at 200
        ("elseif", 400),   // 64 KB SPIRV-Cross from ~15; DXC wrong diagnostic from 200; vkd3d from 75
        ("calls", 1600),   // 64 KB vkd3d: trapped
    ];

    internal static string Body(string kind, int n) => kind switch
    {
        "add" => $"float x = {string.Join(" + ", Enumerable.Repeat("uv.x", n))}; return float4(x,0,0,1);",
        "parens" => $"float x = {new string('(', n)}uv.x{new string(')', n)}; return float4(x,0,0,1);",
        "ternary" => $"float x = {Enumerable.Range(0, n).Aggregate("uv.x", (e, i) => $"(uv.y > {i}.0 ? {e} : uv.x)")}; return float4(x,0,0,1);",
        "ifnest" => "float x = 0.0; " + string.Concat(Enumerable.Range(0, n).Select(i => $"if (uv.x > {i}.0) {{ x += 1.0; ")) + new string('}', n) + " return float4(x,0,0,1);",
        "elseif" => "float x = 0.0; " + string.Join(" else ", Enumerable.Range(0, n).Select(i => $"if (uv.x > {i}.0) x = {i}.0;")) + " return float4(x,0,0,1);",
        "calls" => $"return float4(f{n}(uv.x),0,0,1);",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    internal static string Fx(string kind, int n)
    {
        var sb = new StringBuilder();
        sb.Append("#if OPENGL\n#define SV_POSITION POSITION\n#define PS_SHADERMODEL ps_3_0\n#else\n#define PS_SHADERMODEL ps_4_0\n#endif\n");
        sb.Append("struct VSOut { float4 Position : SV_POSITION; float4 Color : COLOR0; float2 TextureCoordinates : TEXCOORD0; };\n");
        if (kind == "calls")
        {
            sb.Append("float f0(float x) { return x * 1.5; }\n");
            for (int i = 1; i <= n; i++)
                sb.Append($"float f{i}(float x) {{ return f{i - 1}(x) + 1.0; }}\n");
        }
        sb.Append("float4 MainPS(VSOut input) : COLOR0 { float2 uv = input.TextureCoordinates; ")
          .Append(Body(kind, n)).Append(" }\n");
        sb.Append("technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }\n");
        return sb.ToString();
    }

    internal static async Task<int> RunAsync(string repoRoot, string outDir)
    {
        // Same containment guard as the corpus probe: the output directory is swept.
        string outRel = Path.GetRelativePath(repoRoot, outDir);
        if (outRel == "." || outRel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(outRel))
        {
            Console.Error.WriteLine($"DepthProbe: refusing to sweep '{outDir}': not strictly under the repo root '{repoRoot}'.");
            return 1;
        }
        Directory.CreateDirectory(outDir);
        foreach (string stale in Directory.EnumerateFiles(outDir))
            File.Delete(stale);

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var manifest = new List<Dictionary<string, object>>();
        var transpiler = new SpirvCrossGlslTranspiler();

        foreach ((string kind, int depth) in Cases)
        {
            string id = $"{kind}-{depth}";
            string fx = Fx(kind, depth);

            var dxc = new RecordingDxc();
            var gl = await new EffectCompiler(dxcCompilerFactory: () => dxc).CompileAsync(
                fx, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "depth.fx" });
            if (gl.IsFailure || dxc.Spirv is null)
            {
                Console.Error.WriteLine($"DepthProbe: {id} (OpenGL) failed on the desktop: " +
                    (gl.IsFailure ? string.Join(" | ", gl.Error.Select(e => $"{e.Code}: {e.Message}")) : "no SPIR-V compile captured"));
                return gl.IsFailure && gl.Error.Any(e => e.Code == "SD0211") ? 3 : 1;
            }
            var glsl = transpiler.Transpile(dxc.Spirv);
            if (glsl.IsFailure)
            {
                Console.Error.WriteLine($"DepthProbe: {id}: desktop SPIRV-Cross failed: {glsl.Error.Message}");
                return 1;
            }

            var vk = new RecordingVkd3dCompiler();
            var dx = await new EffectCompiler(dxbcCompilerFactory: () => vk).CompileAsync(
                fx, new CompilerOptions { Target = PlatformTarget.DirectX, SourceFileName = "depth.fx", DxbcBackend = DxbcBackend.Vkd3d });
            if (dx.IsFailure || vk.Captures.Count == 0)
            {
                Console.Error.WriteLine($"DepthProbe: {id} (DirectX) failed on the desktop: " +
                    (dx.IsFailure ? string.Join(" | ", dx.Error.Select(e => $"{e.Code}: {e.Message}")) : "no vkd3d compile captured"));
                return dx.IsFailure && dx.Error.Any(e => e.Code == "SD0211") ? 3 : 1;
            }
            (D3DCompileRequest request, byte[] nativeSource, int[] options, _, byte[] dxbc, _) = vk.Captures[^1];
            string profile = Vkd3dCompileContract.ResolveProfile(request);

            File.WriteAllText(Path.Combine(outDir, $"{id}.dxc.hlsl"), dxc.Hlsl!, utf8);
            File.WriteAllBytes(Path.Combine(outDir, $"{id}.spv"), dxc.Spirv);
            File.WriteAllText(Path.Combine(outDir, $"{id}.glsl"), glsl.Value.Text, utf8);
            // The bytes the desktop handed vkd3d (PrepareSource applied, issue #319), read back
            // from the native call, so the shim replays exactly the desktop's text.
            File.WriteAllBytes(Path.Combine(outDir, $"{id}.vkd3d.hlsl"), nativeSource);
            File.WriteAllBytes(Path.Combine(outDir, $"{id}.dxbc"), dxbc);

            manifest.Add(new Dictionary<string, object>
            {
                ["id"] = id,
                ["kind"] = kind,
                ["depth"] = depth,
                ["dxcArgs"] = dxc.Args!,
                ["entryPoint"] = request.EntryPoint,
                ["profile"] = profile,
                ["targetType"] = Vkd3dCompileContract.ResolveTargetType(profile),
                // The vkd3d compile options the desktop passed, as flat (name, value) pairs.
                ["options"] = options,
                ["sourceName"] = request.SourceFileName,
            });
            Console.WriteLine($"DepthProbe: {id}: SPIR-V {dxc.Spirv.Length} B, GLSL {glsl.Value.Text.Length} chars, DXBC {dxbc.Length} B");
        }

        File.WriteAllText(
            Path.Combine(outDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n",
            utf8);
        Console.WriteLine($"DepthProbe: captured {manifest.Count} depth cases into {outDir}");
        return 0;
    }

    /// <summary>Records the OpenGL (SPIR-V) DXC compile: exact HLSL, argument list, output.</summary>
    private sealed class RecordingDxc : IDxcShaderCompiler, IDisposable
    {
        private readonly DxcShaderCompiler _inner = new();
        public string? Hlsl { get; private set; }
        public string[]? Args { get; private set; }
        public byte[]? Spirv { get; private set; }

        public async Task<Result<PlatformBlob, ShaderError>> CompileAsync(DxcCompileRequest request, CancellationToken cancellationToken = default)
        {
            var result = await _inner.CompileAsync(request, cancellationToken).ConfigureAwait(false);
            Record(request, result);
            return result;
        }

        public Result<PlatformBlob, ShaderError> Compile(DxcCompileRequest request, CancellationToken cancellationToken = default)
        {
            var result = _inner.Compile(request, cancellationToken);
            Record(request, result);
            return result;
        }

        public Result<string, ShaderError> Preprocess(DxcPreprocessRequest request, CancellationToken cancellationToken = default) =>
            _inner.Preprocess(request, cancellationToken);

        private void Record(DxcCompileRequest request, Result<PlatformBlob, ShaderError> result)
        {
            if (request.Platform != PlatformTarget.OpenGL || !result.IsSuccess || result.Value.Kind != BlobKind.Spirv)
                return;
            Hlsl = request.HlslSource;
            Args = DxcFlagBuilder.Build(request.Platform, request.Stage, request.EntryPoint, request.Macros, request.Options).ToArray();
            Spirv = result.Value.Bytes.ToArray();
        }

        public void Dispose() => _inner.Dispose();
    }
}
