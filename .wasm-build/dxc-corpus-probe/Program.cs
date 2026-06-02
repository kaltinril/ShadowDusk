using System.Reflection;
using System.Text;
using System.Text.Json;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL.Dxc;

// Ground-truth probe for the DXC->WASM byte-identity gate (Phase 23 M0).
//
// Drives the REAL desktop EffectCompiler over the Phase-17 PS-only corpus. A
// capturing IDxcShaderCompiler decorator wraps the real DxcShaderCompiler and, for
// every OpenGL/SPIR-V compile (Platform=OpenGL), records the EXACT (HLSL, args,
// SPIR-V) triple the desktop pipeline feeds DXC. The DXC arg list is read straight
// from ShadowDusk.HLSL's internal DxcFlagBuilder via reflection so the dumped args
// are byte-for-byte what JsDxcShaderCompiler will pass compileToSpirv in the browser.

string repoRoot = args.Length > 0
    ? args[0]
    // default: walk up from the probe's bin dir to the repo root containing tests/fixtures
    : FindRepoRoot(AppContext.BaseDirectory);

string shadersDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
string outDir = args.Length > 1
    ? args[1]
    : Path.Combine(AppContext.BaseDirectory, "corpus-spirv");
Directory.CreateDirectory(outDir);

// The canonical Phase-17 PS-only corpus (== SpirvReflectionByteIdentityTests.s_corpus).
string[] corpus =
{
    "Grayscale", "Invert", "TintShader", "Sepia", "Saturate",
    "Pixelated", "Scanlines", "Fading", "Dots", "Dissolve",
};

// Reflect DxcFlagBuilder.Build(platform, stage, entryPoint, macros, options) so the
// captured args are EXACTLY the desktop arg list (no reimplementation drift).
MethodInfo buildFlags = typeof(DxcShaderCompiler).Assembly
    .GetType("ShadowDusk.HLSL.Dxc.DxcFlagBuilder", throwOnError: true)!
    .GetMethod("Build", BindingFlags.Public | BindingFlags.Static)
    ?? throw new InvalidOperationException("DxcFlagBuilder.Build not found");

int written = 0;
var manifest = new List<object>();

foreach (string name in corpus)
{
    string fxPath = Path.Combine(shadersDir, name + ".fx");
    if (!File.Exists(fxPath))
    {
        Console.Error.WriteLine($"[{name}] MISSING fixture: {fxPath}");
        Environment.Exit(2);
    }

    string source = await File.ReadAllTextAsync(fxPath);

    var capture = new CapturingDxc(buildFlags);
    var options = new CompilerOptions
    {
        Target          = PlatformTarget.OpenGL,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName  = fxPath,
    };

    // Real pipeline, real DXC — only the DXC stage is wrapped to capture I/O.
    var compiler = new EffectCompiler(dxcCompilerFactory: () => capture);
    var result = await compiler.CompileAsync(source, options);
    if (result.IsFailure)
    {
        Console.Error.WriteLine($"[{name}] compile FAILED: " +
            string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")));
        Environment.Exit(3);
    }

    if (capture.Spirv.Count == 0)
    {
        Console.Error.WriteLine($"[{name}] no OpenGL/SPIR-V compile was captured");
        Environment.Exit(4);
    }

    // PS-only corpus => exactly one OpenGL/SPIR-V compile (the pixel stage). If a
    // shader ever yields >1, suffix by index; corpus today is 1:1.
    for (int i = 0; i < capture.Spirv.Count; i++)
    {
        string stem = capture.Spirv.Count == 1 ? name : $"{name}.{i}";
        var rec = capture.Spirv[i];

        string hlslPath = Path.Combine(outDir, stem + ".hlsl");
        string argsPath = Path.Combine(outDir, stem + ".args.json");
        string spvPath  = Path.Combine(outDir, stem + ".spv");

        // Raw bytes, no BOM, exact newlines — this is the precise DXC input.
        await File.WriteAllTextAsync(hlslPath, rec.Hlsl, new UTF8Encoding(false));
        await File.WriteAllTextAsync(argsPath,
            JsonSerializer.Serialize(rec.Args, new JsonSerializerOptions { WriteIndented = false }),
            new UTF8Encoding(false));
        await File.WriteAllBytesAsync(spvPath, rec.Spirv);

        manifest.Add(new
        {
            name = stem,
            entryPoint = rec.EntryPoint,
            stage = rec.Stage.ToString(),
            spirvBytes = rec.Spirv.Length,
            spirvWords = rec.Spirv.Length / 4,
            args = rec.Args,
        });

        Console.WriteLine($"[{stem}] entry={rec.EntryPoint} spirv={rec.Spirv.Length} bytes " +
            $"({rec.Spirv.Length / 4} words) args=[{string.Join(' ', rec.Args)}]");
        written++;
    }
}

await File.WriteAllTextAsync(
    Path.Combine(outDir, "manifest.json"),
    JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
    new UTF8Encoding(false));

Console.WriteLine($"\nPROBE_OK — {written} corpus SPIR-V triple(s) written to {outDir}");
return;

static string FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")) ||
            Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "shaders")))
            return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("Could not locate repo root from " + start);
}

// Wraps the real DxcShaderCompiler; records every OpenGL/SPIR-V compile's
// (preprocessed HLSL, exact DXC args, SPIR-V bytes). The OpenGL pipeline also runs
// a DirectX-target DXC compile purely for DXIL reflection — we ignore those and
// keep only the SPIR-V (Platform=OpenGL) compiles, which are the browser path.
sealed class CapturingDxc(MethodInfo buildFlags) : IDxcShaderCompiler, IDisposable
{
    private readonly DxcShaderCompiler _inner = new();
    public List<(string Hlsl, string[] Args, byte[] Spirv, string EntryPoint, ShaderStage Stage)> Spirv { get; } = new();

    public async Task<Result<PlatformBlob, ShaderError>> CompileAsync(
        DxcCompileRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _inner.CompileAsync(request, cancellationToken);

        if (request.Platform == PlatformTarget.OpenGL && result.IsSuccess &&
            result.Value.Kind == BlobKind.Spirv)
        {
            // Same call DxcShaderCompiler makes internally => identical args.
            var argList = (System.Collections.Generic.IReadOnlyList<string>)buildFlags.Invoke(
                null,
                new object?[] { request.Platform, request.Stage, request.EntryPoint, request.Macros, request.Options })!;

            Spirv.Add((
                request.HlslSource,
                argList.ToArray(),
                result.Value.Bytes.ToArray(),
                request.EntryPoint,
                request.Stage));
        }

        return result;
    }

    public void Dispose() => _inner.Dispose();
}
