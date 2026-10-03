#nullable enable

// Vkd3dCorpusProbe — the DESKTOP ground-truth half of the Phase 4.1 vkd3d→WASM
// byte-identity gate (the Phase 23 G0/G1 `dxc-corpus-probe` pattern, for vkd3d).
//
// It drives the REAL product pipeline (EffectCompiler) over the byte-identity corpus
// for the DirectX (SM5 → DXBC_TPF) and FNA (SM1–3 → D3D_BYTECODE) targets, with the
// desktop vkd3d backend (Vkd3dShaderCompiler) wrapped in a recording decorator
// injected through the Phase 4.1 `dxbcCompilerFactory` seam — the SAME seam the WASM
// host uses. Every vkd3d compile the pipeline performs is captured EXACTLY as issued:
// the source bytes the desktop REALLY handed vkd3d_shader_compile (read back from the
// marshalled compile info through Vkd3dShaderCompiler.NativeSourceObserver: the
// preprocessed HLSL after Vkd3dCompileContract.PrepareSource, issue #319) beside the
// directive-carrying request text the pipeline produced, the entry point, the resolved
// profile/target type (via the shared Vkd3dCompileContract), the vkd3d compile options
// the desktop REALLY handed vkd3d_shader_compile (read back the same way through
// NativeOptionsObserver, issue #295), and the output bytes the desktop native produced.
//
// node-test-vkd3d-wasm.mjs then replays each captured request through the product
// WASM shim (src/ShadowDusk.Wasm/wwwroot/shadowdusk-vkd3d.js) and asserts the output
// byte-identical — proving the vkd3d→WASM build IS the desktop compiler, at the only
// seam that differs between hosts.
//
// Usage: dotnet run --project Vkd3dCorpusProbe -- <repoRoot> <outDir>
// Exit codes: 0 = captured OK; 3 = desktop vkd3d native unavailable (SD0211 —
// caller should SKIP loudly, not fail); 1 = real failure.

using System.Text;
using System.Text.Json;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Vkd3d;

// Issue #271: `--depth <repoRoot> <outDir>` captures the stack-depth cases instead
// (DepthProbe.cs, consumed by node-test-wasm-depth.mjs).
if (args.Length == 3 && args[0] == "--depth")
    return await DepthProbe.RunAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: Vkd3dCorpusProbe <repoRoot> <outDir>");
    return 1;
}

string repoRoot = Path.GetFullPath(args[0]);
string outDir   = Path.GetFullPath(args[1]);
string fixtures = Path.Combine(repoRoot, "tests", "fixtures", "shaders");

if (!Directory.Exists(fixtures))
{
    Console.Error.WriteLine($"Vkd3dCorpusProbe: fixtures not found at {fixtures}");
    return 1;
}

// -----------------------------------------------------------------------------
// Corpus — the SAME lists CrossHostByteIdentityTests pins (keep in sync with
// tests/ShadowDusk.Integration.Tests/Tests/CrossHostByteIdentityTests.cs):
// the MGFX baseline corpus (DX SM5) plus the SM ≤ 3 render-proven corpus (DX + FNA).
//
// Sm3SemanticStructs.fx (issue #295) is the one fixture whose DXBC depends on the vkd3d
// compile options: SM1-3 semantics on struct fields, which only
// BACKWARD_COMPATIBILITY = MAP_SEMANTIC_NAMES turns into SV_Position / SV_Target. Every
// other fixture here compiles to the same bytes with or without the option, which is
// how the browser module passing NO options went unnoticed by this gate. It must stay.
// -----------------------------------------------------------------------------

string[] coreMgfxFixtures =
[
    "Minimal.fx", "textured.fx", "cbuffer.fx", "multipass.fx", "multitechnique.fx",
    "render-states.fx", "annotations.fx", "platform-macros.fx", "basiceffect-mini.fx",
];

string[] sm3Fixtures =
[
    "Grayscale.fx", "Invert.fx", "Sepia.fx", "Saturate.fx", "Pixelated.fx",
    "Scanlines.fx", "Fading.fx", "Dots.fx", "Dissolve.fx", "TintShader.fx",
    "BasicShader.fx", "BlendShader.fx", "ClipShader.fx", "ClipShaderNew.fx",
    "ClipShaderSpriteTarget.fx", "MultiTexture.fx", "MultiTextureOverlay.fx",
    "SimpleLightShader.fx", "SpriteAlphaTest.fx", "Teleport.fx",
    "PolygonLight.fx", "VertexAndPixel.fx", "VsTransformColorTexture.fx",
    "FnaMultiPassStates.fx",
    "examples/ExBareSamplerTex2D.fx", "examples/ExSamplerStateUniform.fx",
    "examples/ExDualTexture.fx", "examples/ExLegacyTextureDiscard.fx",
    "Sm3SemanticStructs.fx",
];

// Fixtures the DirectX arm must NOT carry, because the DirectX target legitimately
// refuses them (Phase 51 A10, SD0015): a pass naming an SM <= 3 compile target is
// rejected by `mgfxc /Profile:DirectX_11` — "Invalid profile 'vs_2_0'. Vertex shader
// 'MainVS' must be SM 4.0 level 9.1 or higher!" — so ShadowDusk rejects it too.
// Capturing DirectX ground truth for a shader the reference compiler cannot build was
// capturing bytes no consumer can ever obtain from mgfxc. It stays in the FNA arm,
// where SM2 is exactly right. Mirrors `CrossHostByteIdentityTests.DirectXExcluded`;
// the two corpora are kept deliberately in step (this file's header says so).
string[] directXExcluded =
[
    "FnaMultiPassStates.fx",   // 'compile vs_2_0 MainVS()' / 'compile ps_2_0 …'
];

var directXCorpus = coreMgfxFixtures.Concat(sm3Fixtures)
                                    .Where(fx => !directXExcluded.Contains(fx, StringComparer.Ordinal))
                                    .ToArray();
var fnaCorpus     = sm3Fixtures;

// Containment guard: the stale-file sweep below DELETES every file in outDir, so a
// mistyped/hostile argument (e.g. the user's home directory) must be refused before
// anything is touched. The probe only ever writes inside the repo, so require outDir
// to be strictly under the repo root.
string outRel = Path.GetRelativePath(repoRoot, outDir);
if (outRel == "." || outRel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(outRel))
{
    Console.Error.WriteLine(
        $"Vkd3dCorpusProbe: refusing to sweep '{outDir}' — it is not strictly under the " +
        $"repo root '{repoRoot}'. The output directory is cleared of stale files before " +
        "capture, so it must be a repo-local scratch directory.");
    return 1;
}

Directory.CreateDirectory(outDir);
foreach (string stale in Directory.EnumerateFiles(outDir))
    File.Delete(stale);

var recorder = new RecordingVkd3dCompiler();
var compiler = new EffectCompiler(dxbcCompilerFactory: () => recorder);
var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
var manifest = new List<Dictionary<string, object>>();
bool nativeMissing = false;

async Task<int> CompileCorpusAsync(string[] corpus, PlatformTarget target)
{
    int compiled = 0;
    foreach (string fx in corpus)
    {
        // EOL-normalize exactly like CrossHostByteIdentityTests: the gate must feed
        // both backends identical input bytes regardless of git checkout EOL flavor.
        string source = (await File.ReadAllTextAsync(Path.Combine(fixtures, fx)))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        int before = recorder.Captures.Count;
        var result = await compiler.CompileAsync(source, new CompilerOptions
        {
            Target         = target,
            SourceFileName = fx,                 // fixed relative name, never a host path
            DxbcBackend    = DxbcBackend.Vkd3d,  // ignored (factory injected); for clarity
        });

        if (result.IsFailure)
        {
            if (result.Error.Any(e => e.Code == "SD0211"))
            {
                nativeMissing = true;
                return -1;
            }

            Console.Error.WriteLine($"Vkd3dCorpusProbe: '{fx}' ({target}) FAILED: " +
                string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")));
            return 1;
        }

        // Persist the captures this fixture produced (one per vkd3d stage compile).
        for (int i = before; i < recorder.Captures.Count; i++)
        {
            (D3DCompileRequest request, byte[] nativeSource, int[] options, byte[] output) = recorder.Captures[i];
            string id = $"{manifest.Count:D3}";
            string profile = Vkd3dCompileContract.ResolveProfile(request);

            // The EXACT bytes the desktop handed vkd3d_shader_compile (read back from the
            // native call: request.HlslSource after Vkd3dCompileContract.PrepareSource, so
            // every #line directive line is blank) — node passes these to the shim verbatim,
            // so both hosts hand vkd3d identical input bytes at the seam that differs.
            File.WriteAllBytes(Path.Combine(outDir, $"{id}.hlsl"), nativeSource);
            // The directive-carrying text the pipeline produced (what the browser host used
            // to hand vkd3d before issue #319), UTF-8 without BOM: the node gate's control
            // that the directives change no output byte and cost one fixme line each.
            File.WriteAllText(Path.Combine(outDir, $"{id}.request.hlsl"), request.HlslSource, utf8NoBom);
            File.WriteAllBytes(Path.Combine(outDir, $"{id}.bin"), output);

            manifest.Add(new Dictionary<string, object>
            {
                ["id"]         = id,
                ["fixture"]    = fx,
                ["target"]     = target.ToString(),
                ["entryPoint"] = request.EntryPoint,
                ["stage"]      = request.Stage.ToString(),
                ["profile"]    = profile,
                ["targetType"] = Vkd3dCompileContract.ResolveTargetType(profile),
                // What the DESKTOP passed, as flat (name, value) pairs: the node gate hands
                // exactly this to the product shim.
                ["options"]    = options,
                ["sourceName"] = request.SourceFileName,
                ["sourceFile"] = $"{id}.hlsl",
                // The request text before preparation (issue #319 control).
                ["requestSourceFile"] = $"{id}.request.hlsl",
                ["blobFile"]   = $"{id}.bin",
                ["blobBytes"]  = output.Length,
            });
        }

        compiled++;
    }

    Console.WriteLine($"Vkd3dCorpusProbe: {target} — {compiled}/{corpus.Length} fixtures compiled");
    return 0;
}

int rc = await CompileCorpusAsync(directXCorpus, PlatformTarget.DirectX);
if (rc == 0)
    rc = await CompileCorpusAsync(fnaCorpus.ToArray(), PlatformTarget.Fna);

if (nativeMissing)
{
    Console.Error.WriteLine(
        "Vkd3dCorpusProbe: desktop vkd3d-shader native not found (SD0211) — run " +
        "tools/restore.ps1 / tools/restore.sh. Signalling SKIP (exit 3).");
    return 3;
}

if (rc != 0)
    return 1;

File.WriteAllText(
    Path.Combine(outDir, "manifest.json"),
    JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true })
        .ReplaceLineEndings("\n") + "\n",
    utf8NoBom);

Console.WriteLine($"Vkd3dCorpusProbe: captured {manifest.Count} vkd3d compiles into {outDir}");
return 0;

/// <summary>
/// Records every (request, native source, options, output) tuple the pipeline sends through
/// the desktop vkd3d backend — the ground truth the WASM shim must reproduce byte-for-byte.
/// The source bytes and the options are the ones the desktop REALLY handed
/// <c>vkd3d_shader_compile</c>, read back from the marshalled compile info
/// (<see cref="Vkd3dShaderCompiler.NativeSourceObserver"/>, issue #319;
/// <see cref="Vkd3dShaderCompiler.NativeOptionsObserver"/>, flat (name, value) pairs).
/// </summary>
internal sealed class RecordingVkd3dCompiler : IDxbcShaderCompiler
{
    private readonly Vkd3dShaderCompiler _inner = new();

    public List<(D3DCompileRequest Request, byte[] NativeSource, int[] Options, byte[] Output)> Captures { get; } = [];

    public async Task<Result<PlatformBlob, ShaderError>> CompileAsync(
        D3DCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        // The first native call of a compile is the real one (any later ones are
        // diagnostic-relocation probes of the same request, with the same options).
        byte[]? source = null;
        int[]? options = null;
        Vkd3dShaderCompiler.NativeSourceObserver.Value = s => source ??= s;
        Vkd3dShaderCompiler.NativeOptionsObserver.Value = o => options ??= o;
        try
        {
            var result = await _inner.CompileAsync(request, cancellationToken).ConfigureAwait(false);
            Record(request, source, options, result);
            return result;
        }
        finally
        {
            Vkd3dShaderCompiler.NativeSourceObserver.Value = null;
            Vkd3dShaderCompiler.NativeOptionsObserver.Value = null;
        }
    }

    public Result<PlatformBlob, ShaderError> Compile(
        D3DCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        byte[]? source = null;
        int[]? options = null;
        Vkd3dShaderCompiler.NativeSourceObserver.Value = s => source ??= s;
        Vkd3dShaderCompiler.NativeOptionsObserver.Value = o => options ??= o;
        try
        {
            var result = _inner.Compile(request, cancellationToken);
            Record(request, source, options, result);
            return result;
        }
        finally
        {
            Vkd3dShaderCompiler.NativeSourceObserver.Value = null;
            Vkd3dShaderCompiler.NativeOptionsObserver.Value = null;
        }
    }

    private void Record(D3DCompileRequest request, byte[]? source, int[]? options, Result<PlatformBlob, ShaderError> result)
    {
        if (!result.IsSuccess)
            return;
        Captures.Add((
            request,
            source ?? throw new InvalidOperationException(
                "the desktop vkd3d backend compiled without reporting the source it handed vkd3d " +
                "(Vkd3dShaderCompiler.NativeSourceObserver was never called)"),
            options ?? throw new InvalidOperationException(
                "the desktop vkd3d backend compiled without reporting its compile options " +
                "(Vkd3dShaderCompiler.NativeOptionsObserver was never called)"),
            result.Value.Bytes.ToArray()));
    }
}
