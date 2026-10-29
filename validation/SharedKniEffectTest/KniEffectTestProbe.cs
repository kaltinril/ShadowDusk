#nullable enable

// KNI's own effect test suite as a ShadowDusk render gate, in the REAL engine.
//
// Ports every test of KNI's Tests/Framework.Graphics/EffectTest.cs (github.com/kniEngine/kni, Ms-PL;
// written and checked against XNA by KNI's author) to a real MonoGame/KNI Game and runs the
// shader-driven ones against ShadowDusk's in-process build of KNI's own test shaders, vendored
// unmodified under tests/fixtures/shaders/third-party/KNI. Five companion shaders
// (validation/SharedKniEffectTest/Companion, ShadowDusk-authored) pin the extra findings of the
// evaluation that ported the suite (matrix default layout, a texture at register(t1), a texture array,
// vertex texture fetch in both spellings).
//
// Builds ("arms") of each shader:
//   SD-MGFX / SD-KNIFX        ShadowDusk, from the vendored source (KNIFX on the KNI runtimes only).
//   SD-MGFX-l9 / SD-KNIFX-l9  DirectX 11 only: the vendored source with the `compile` profile token
//                             rewritten s_2_0 -> s_4_0_level_9_1 and s_3_0 -> s_4_0_level_9_3, which is
//                             exactly what KNI's own DX11 content pipeline does (ShaderProfileDX11).
//   mgfxc                     the committed real-mgfxc build (tests/fixtures/golden/kni-effecttest).
//   kni-xnb                   KNI's own prebuilt reference .xnb, through a real ContentManager (KNI only).
//
// Every (case, check, arm) row is one of:
//   GATE        must pass; a failure turns the gate red.
//   KNOWN-FAIL  a listed ShadowDusk bug being fixed elsewhere; printed with its reason, and the gate
//               turns red if it unexpectedly PASSES, so the fixing PR has to flip it to GATE here.
//   INFO        a runtime-only test, a runtime bug, an inherent MGFX limit, a deliberate deviation, or a
//               reference arm: printed with the reason, never scored.
// Plus positive controls (planted defects) that must FAIL, or the harness is not seeing what it claims.
//
// Pixel tolerance: KNI's test asserts an exact `== new Color(0f, 0.5f, 1f, 1f)`, i.e. G == 128. 0.5 * 255
// is 127.5, and real rasterizers round it either way (KNI's own prebuilt build reads 127 on a GPU too),
// so every pixel check here allows +-1 per channel, the tolerance the other render gates use for a
// rounding-only difference. Exit code 0 iff every GATE row passes, no KNOWN-FAIL row passes, every
// control is detected and every row ran. See docs/validation-matrix.md section 6.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
// KNI WinForms.DX11 pulls System.Drawing into the implicit usings: name the XNA types.
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace ShadowDusk.Validation.KniEffectTest;

/// <summary>What one driver runs: the runtime it is linked against and which arms it builds.</summary>
public sealed class ProbeConfig
{
    /// <summary>Runtime id the expectations are keyed on: kni-gl, kni-dx, mg-gl, mg-dx, mg-dx12, mg-vk.</summary>
    public required string Runtime { get; init; }
    public required PlatformTarget Target { get; init; }
    public required EffectContainer[] Containers { get; init; }
    /// <summary>Folder under tests/fixtures/golden/kni-effecttest holding the mgfxc builds, or null.</summary>
    public string? MgfxcGoldenDir { get; init; }
    /// <summary>Folder under third-party/KNI holding KNI's prebuilt .xnb files (KNI runtimes), or null.</summary>
    public string? KniXnbDir { get; init; }
    /// <summary>Runs on the GraphicsDeviceManager before the device exists (the WARP pin on KNI DX).</summary>
    public Action<GraphicsDeviceManager>? ConfigureDevice { get; init; }
    /// <summary>When set, the loaded runtime's assembly version must start with it (KNI 4.2 vs 4.3 builds).</summary>
    public string? ExpectedRuntimeVersion { get; init; }
}

public static class KniEffectTestProbe
{
    // ----------------------------------------------------------------------------------------------
    // Cases. A KNI case is one EffectTest method; Fx is its vendored shader (null = runtime-only).
    // ----------------------------------------------------------------------------------------------

    private enum Source { Kni, Companion, None }

    private sealed record CaseDef(string Id, string Test, Source Source, string? Fx, string[] Checks);

    private static readonly CaseDef[] Cases =
    [
        new("BasicEffect.ParamByIndex", "EffectParameterShouldBeSetIfSetByNameAndGetByIndex", Source.None, null, ["value"]),
        new("BasicEffect.SetTexture", "EffectPassShouldSetTexture", Source.None, null, ["bind"]),
        new("BasicEffect.SetTextureTwice", "EffectPassShouldSetTextureOnSubsequentCalls", Source.None, null, ["bind"]),
        new("BasicEffect.SetTextureNull", "EffectPassShouldSetTextureEvenIfNull", Source.None, null, ["bind"]),
        new("BasicEffect.OverrideTexture", "EffectPassShouldOverrideTextureIfNotExplicitlySet", Source.None, null, ["bind"]),
        new("OldSyntax_x", "OldSyntaxEffectPassShouldNotOverrideSamplersAndNotOverrideTextures", Source.Kni, "OldSyntax_x", ["textures", "samplers"]),
        new("OldSyntax_s0s1", "OldSyntaxEffectPassShouldOverrideSamplersAndNotOverrideTextures", Source.Kni, "OldSyntax_s0s1", ["textures", "samplers"]),
        new("OldSyntax_t0t1", "OldSyntaxEffectPassShouldSetTexturesAndNotSamplers", Source.Kni, "OldSyntax_t0t1", ["bind", "render"]),
        new("OldSyntax_t1t0", "OldSyntaxEffectPassShouldSetTexturesAndNotSamplers2", Source.Kni, "OldSyntax_t1t0", ["bind", "render"]),
        new("OldSyntax_s0s1t0t1", "OldSyntaxEffectPassShouldSetTexturesAndSamplers", Source.Kni, "OldSyntax_s0s1t0t1", ["bind"]),
        new("DeferredBasicEffect", "DeferredBasicEffectPassShouldSetTexturesAndSamplers", Source.Kni, "DeferredBasicEffect", ["bind"]),
        new("NewSyntax_t0t1", "NewSyntaxEffectPassShouldSetTexturesAndNotSamplers", Source.Kni, "NewSyntax_t0t1", ["bind"]),
        new("NewSyntax_s0s1t0t1", "NewSyntaxEffectPassShouldSetTexturesAndSamplers", Source.Kni, "NewSyntax_s0s1t0t1", ["bind"]),
        new("ComputeShader", "ShouldCompileComputeShader", Source.Kni, "ComputeShader", ["load"]),
        new("OptimizedMatrix", "ShouldSetAndGetOptimizedMatrix", Source.Kni, "OptimizedMatrix", ["default", "roundtrip", "warnings"]),
        new("OptimizedMatrixArray", "ShouldSetAndGetOptimizedMatrixArray", Source.Kni, "OptimizedMatrixArray", ["roundtrip"]),
        new("MatrixBrace", "companion: float4x4 brace-list default renders as the HLSL initializer", Source.Companion, "MatrixBrace", ["render"]),
        new("TextureAtRegisterT1", "companion: a lone Texture2D at register(t1) samples its parameter", Source.Companion, "TextureAtRegisterT1", ["render"]),
        new("TextureArraySlice", "companion: Texture2DArray slice 1 is sampled", Source.Companion, "TextureArraySlice", ["render"]),
        new("VertexTextureSampleLevel", "companion: vertex texture fetch (SampleLevel in the VS)", Source.Companion, "VertexTextureSampleLevel", ["render"]),
        new("VertexTextureTex2Dlod", "companion: vertex texture fetch (tex2Dlod in the VS, XNA spelling)", Source.Companion, "VertexTextureTex2Dlod", ["render"]),
    ];

    private const string PassToken = "PASS";

    // ----------------------------------------------------------------------------------------------
    // Expectations. Default: every ShadowDusk row is GATE. The rules below are the measured exceptions,
    // each with the reason printed next to the row. Reference arms are always INFO.
    // ----------------------------------------------------------------------------------------------

    internal enum Kind { Gate, KnownFail, Info }

    internal sealed record Expectation(Kind Kind, string Reason);

    // Known ShadowDusk bugs (fixed on other branches). A row tagged with one must still FAIL.
    private const string KfKnifxDxSm2 =
        "known bug: KNIFX DirectX refuses a ps_2_0/vs_2_0/ps_3_0 profile with SD0015, but KNI's own DX11 pipeline accepts it and builds it at SM 4.0 level 9.x (ShaderProfileDX11)";
    private const string KfKnifxLevel9 =
        "known bug (fixed in PR #430): KNIFX DirectX records a *_4_0_level_9_x profile as SM 4.0, which KNI refuses on the HiDef profile ('Shader model 4.0 is not supported')";
    private const string KfGlVertexSemantics =
        "known bug: OpenGL refuses BINORMAL/TANGENT/BLENDINDICES/BLENDWEIGHT vertex inputs with SD0210 (mgfxc and KNI map them to vertex attributes)";
    private const string KfKnifxTexturelessSampler =
        "known bug: KNIFX exposes a texture-less legacy sampler2D as a Texture2D parameter; KNI's own KNIFX (and XNA) expose none, so Apply must not overwrite Textures[n]";
    private const string KfVulkanRegisterSlots =
        "known bug: Vulkan ignores an explicit register(tN): the texture is bound to the wrong descriptor, mgfxc 3.8.5 honours it";
    private const string KfTex2Dlod =
        "known bug: tex2Dlod is refused with FX0012 (no 1:1 modern rewrite yet); mgfxc compiles it for DirectX_11 and DirectX_12";

    // Informational reasons.
    private const string InfoRuntimeOnly =
        "runtime-only: exercises BasicEffect, the runtime's built-in effect; ShadowDusk's output is not involved";
    private const string InfoMgfxSm2Dx =
        "matches mgfxc: MGFX DirectX_11 refuses a ps_2_0/vs_2_0 profile with SD0015, exactly as mgfxc does (the -l9 arm is the gated build)";
    private const string InfoMgfxTexturelessSampler =
        "inherent MGFX limit: a texture-less sampler2D becomes a Texture2D parameter in MGFX (mgfxc's OpenGL and DirectX_11 builds do the same), so Apply binds it over Textures[n]; XNA and KNIFX expose none";
    private const string InfoMatrixDefault =
        "deliberate deviation (project_decisions.md, initializer defaults): ShadowDusk reads the brace list as HLSL does (row by row); XNA's fx_2_0 effects and mgfxc DirectX_11 return it transposed, which is what KNI's test asserts";
    private const string InfoCompute =
        "not implemented: ShadowDusk emits no compute stage (MGFX v10 has none, KNIFX compute output is not modelled); mgfxc 3.8.4.1/3.8.5 refuse the file too";
    private const string InfoGlVtf =
        "runtime limit: MonoGame and KNI GL both translate through MojoShader, which has no vertex texture fetch (SD0210); mgfxc's OpenGL build fails the same way";
    private const string InfoGlTextureArray =
        "runtime limit: MojoShader (MonoGame and KNI GL) has no texture-array sampler (SD0210); mgfxc OpenGL refuses it (X4545)";
    private const string InfoKniHiDefTextureArray =
        "runtime limit: KNI WinForms.DX11 refuses to create a texture array on the HiDef profile ('Texture arrays are not supported on this graphics device'); mgfxc's build hits the same";
    private const string InfoDx12TextureArraySlice =
        "runtime bug: MonoGame 3.8.5 WindowsDX12 samples slice 0 of a Texture2D(..., arraySize: 2) (red, not the green slice 1); mgfxc 3.8.5's build renders the same red";
    private const string InfoKniDxVtf =
        "runtime bug: KNI WinForms.DX11 throws IndexOutOfRangeException drawing an MGFX effect whose vertex shader samples a texture; mgfxc's build throws the same";
    private const string InfoKnifxSm4HiDef =
        "runtime limit: KNI refuses a real SM 4.0 shader on the HiDef profile (it needs GraphicsProfile.FL10_0); KNI's own pipeline output behaves the same";
    private const string InfoDx12LegacyColorStruct =
        "open gap (docs/validation-matrix.md section 7): DirectX 12 refuses COLORn members of a pixel-shader OUTPUT struct ('Semantic COLOR is invalid for shader model: ps'); mgfxc 3.8.5 compiles the file at ps_6_0, but its effect exposes no parameters at all, so it fails this test too";
    private const string InfoVulkanLegacyColorStruct =
        "matches mgfxc: Vulkan refuses COLORn members of a pixel-shader output struct at SM 6 (DXC's SPIR-V path); mgfxc 3.8.5 /Profile:Vulkan refuses the file too";
    private const string InfoDx12VsPsLinkage =
        "runtime constraint: the test draws KNI's PS (reads TEXCOORD0) behind BasicEffect's VS (writes none); D3D12 pipeline creation rejects that pairing (HRESULT 0x80070057) where D3D11 tolerates it, so MonoGame 3.8.5 WindowsDX12 throws for ShadowDusk's PS and draws nothing for mgfxc 3.8.5's";
    private const string InfoGlModernSamplerRegister =
        "matches mgfxc: on OpenGL a modern `SamplerState : register(s1)` only reserves s1, so the combined pair lands on unit 0, SpriteBatch's texture (docs/glsl-uniform-naming.md); mgfxc's own build renders the same white";
    private const string InfoVulkanVsPsLinkage =
        "not drawn on Vulkan: the test draws KNI's PS (reads a float2 TEXCOORD0 at location 0) behind BasicEffect's VS (writes a float4 there), which the Vulkan spec forbids without VK_KHR_maintenance4 (VUID-RuntimeSpirv-maintenance4-06817, measured under the Khronos validation layer) and MonoGame DesktopVK does not enable it; drawing it would fail the CI lane's layer check for any compiler's PS";
    private const string InfoReference = "reference arm (not scored): the reference compiler's own build, for comparison";

    internal static Expectation Classify(string rt, string kase, string arm, string check)
    {
        if (arm is "mgfxc" or "kni-xnb") return new(Kind.Info, InfoReference);
        CaseDef def = Cases.Single(c => c.Id == kase);
        if (def.Source == Source.None) return new(Kind.Info, InfoRuntimeOnly);

        bool dx11 = rt is "kni-dx" or "mg-dx";
        bool gl = rt is "kni-gl" or "mg-gl";
        bool knifx = arm.StartsWith("SD-KNIFX", StringComparison.Ordinal);
        bool level9 = arm.EndsWith("-l9", StringComparison.Ordinal);
        var known = new List<string>();

        // --- Compile/load blockers that hit every check of an arm -------------------------------------
        if (def.Source == Source.Kni && kase != "ComputeShader")
        {
            if (dx11 && !knifx && !level9) return new(Kind.Info, InfoMgfxSm2Dx);
            if (dx11 && knifx && !level9) known.Add(KfKnifxDxSm2);
            if (dx11 && knifx && level9) known.Add(KfKnifxLevel9);
        }

        switch (kase)
        {
            case "ComputeShader":
                return new(Kind.Info, InfoCompute);

            case "OldSyntax_x" or "OldSyntax_s0s1" when check == "textures":
                if (!knifx) return new(Kind.Info, InfoMgfxTexturelessSampler);
                known.Add(KfKnifxTexturelessSampler);
                break;

            case "DeferredBasicEffect":
                if (gl) known.Add(KfGlVertexSemantics);
                if (rt == "mg-dx12") return new(Kind.Info, InfoDx12LegacyColorStruct);
                if (rt == "mg-vk") return new(Kind.Info, InfoVulkanLegacyColorStruct);
                break;

            case "OldSyntax_t0t1" or "OldSyntax_t1t0" when check == "render" && rt == "mg-dx12":
                return new(Kind.Info, InfoDx12VsPsLinkage);

            case "OldSyntax_t0t1" or "OldSyntax_t1t0" when check == "render" && rt == "mg-vk":
                return new(Kind.Info, InfoVulkanVsPsLinkage);

            case "OptimizedMatrix" when check == "default":
                // Informational on every arm: it stays a mismatch with XNA after any blocker is fixed.
                return new(Kind.Info, InfoMatrixDefault);

            case "MatrixBrace":
                if (dx11 && knifx) known.Add(KfKnifxLevel9);
                break;

            case "TextureAtRegisterT1":
                if (gl) return new(Kind.Info, InfoGlModernSamplerRegister);
                if (rt == "mg-vk") known.Add(KfVulkanRegisterSlots);
                if (dx11 && knifx) return new(Kind.Info, InfoKnifxSm4HiDef);
                break;

            case "TextureArraySlice":
                if (gl) return new(Kind.Info, InfoGlTextureArray);
                if (rt == "kni-dx") return new(Kind.Info, knifx ? InfoKnifxSm4HiDef : InfoKniHiDefTextureArray);
                if (rt == "mg-dx12") return new(Kind.Info, InfoDx12TextureArraySlice);
                if (rt == "mg-vk") known.Add(KfVulkanRegisterSlots); // it sits at register(t1) as well
                break;

            case "VertexTextureSampleLevel":
                if (gl) return new(Kind.Info, InfoGlVtf);
                if (rt == "kni-dx") return new(Kind.Info, knifx ? InfoKnifxSm4HiDef : InfoKniDxVtf);
                break;

            case "VertexTextureTex2Dlod":
                if (gl) return new(Kind.Info, InfoGlVtf);
                if (rt == "kni-dx") return new(Kind.Info, knifx ? InfoKnifxSm4HiDef : InfoKniDxVtf);
                known.Add(KfTex2Dlod);
                break;
        }

        return known.Count > 0 ? new(Kind.KnownFail, string.Join(" + ", known)) : new(Kind.Gate, "");
    }

    // ----------------------------------------------------------------------------------------------
    // Builds
    // ----------------------------------------------------------------------------------------------

    private sealed record Build(CaseDef Case, string Arm, Func<GraphicsDevice, Effect>? Load, string? Error, string[] Warnings, bool IsControl = false, string? ControlCheck = null, string? ControlName = null);

    internal sealed record Row(string Case, string Check, string Arm, bool Pass, string Detail, string? ControlName = null);

    private static readonly Regex ProfileToken = new(@"\bcompile\s+([vp]s)_([23])_0\b", RegexOptions.CultureInvariant);

    /// <summary>KNI's ShaderProfileDX11: s_2_0 -> s_4_0_level_9_1, s_3_0 -> s_4_0_level_9_3, on the compile token.</summary>
    private static string KniDx11Profiles(string src) =>
        ProfileToken.Replace(src, m => $"compile {m.Groups[1].Value}_4_0_level_9_{(m.Groups[2].Value == "2" ? "1" : "3")}");

    public static int Run(ProbeConfig cfg)
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(300_000);
            Console.WriteLine($"[{cfg.Runtime}] WATCHDOG: killing");
            Environment.Exit(99);
        }) { IsBackground = true };
        watchdog.Start();

        string repo = FindRepoRoot();
        string kniDir = Path.Combine(repo, "tests", "fixtures", "shaders", "third-party", "KNI");
        string companionDir = Path.Combine(repo, "validation", "SharedKniEffectTest", "Companion");
        string goldenRoot = Path.Combine(repo, "tests", "fixtures", "golden", "kni-effecttest");
        AssemblyName rtName = typeof(Game).Assembly.GetName();
        Console.WriteLine($"[{cfg.Runtime}] runtime: {rtName.Name} {rtName.Version}");
        if (cfg.ExpectedRuntimeVersion is { } want && !(rtName.Version?.ToString() ?? "").StartsWith(want, StringComparison.Ordinal))
        {
            Console.WriteLine($"[{cfg.Runtime}] FAIL: built for {want} but loaded {rtName.Version} (stale build?)");
            return 1;
        }

        var compiler = new EffectCompiler();
        var builds = new List<Build>();
        bool dx11 = cfg.Target == PlatformTarget.DirectX;

        foreach (CaseDef c in Cases)
        {
            if (c.Source == Source.None) { builds.Add(new Build(c, "runtime", null, null, [])); continue; }
            string path = Path.Combine(c.Source == Source.Kni ? kniDir : companionDir, c.Fx + ".fx");
            string src = File.ReadAllText(path);
            foreach (EffectContainer container in cfg.Containers)
            {
                string tag = container == EffectContainer.Knifx ? "SD-KNIFX" : "SD-MGFX";
                builds.Add(CompileBuild(compiler, cfg, c, tag, path, src));
                if (dx11 && c.Source == Source.Kni)
                    builds.Add(CompileBuild(compiler, cfg, c, tag + "-l9", path, KniDx11Profiles(src)));
            }

            if (cfg.MgfxcGoldenDir is not null)
            {
                string golden = Path.Combine(goldenRoot, cfg.MgfxcGoldenDir, c.Fx + ".mgfx");
                if (File.Exists(golden))
                {
                    byte[] bytes = File.ReadAllBytes(golden);
                    builds.Add(new Build(c, "mgfxc", gd => new Effect(gd, bytes), null, []));
                }
                else
                {
                    builds.Add(new Build(c, "mgfxc", null, "no mgfxc build: mgfxc refused the shader (see tests/fixtures/golden/kni-effecttest/README.md)", []));
                }
            }

            if (cfg.KniXnbDir is not null && c.Source == Source.Kni)
            {
                string root = Path.Combine(kniDir, cfg.KniXnbDir);
                if (File.Exists(Path.Combine(root, c.Fx + ".xnb")))
                    builds.Add(new Build(c, "kni-xnb", gd => LoadXnb(gd, root, c.Fx!), null, []));
            }
        }

        // Positive controls: planted defects that the checks MUST catch, on the gated arm.
        string primaryArm = dx11 ? "SD-MGFX-l9" : "SD-MGFX";
        CaseDef t0t1 = Cases.Single(c => c.Id == "OldSyntax_t0t1");
        string t0t1Src = File.ReadAllText(Path.Combine(kniDir, "OldSyntax_t0t1.fx"));
        if (dx11) t0t1Src = KniDx11Profiles(t0t1Src);
        string oneTexture = Replace(t0t1Src, "Texture = (Texture1);", "Texture = (Texture0); MAGFILTER = POINT; MINFILTER = POINT; MIPFILTER = POINT;");
        builds.Add(CompileBuild(compiler, cfg, t0t1, primaryArm, "CONTROL-one-texture-point.fx", oneTexture) with
        { IsControl = true, ControlCheck = "bind", ControlName = "Sampler1 reads Texture0 with POINT states: bind must fail" });
        if (Classify(cfg.Runtime, "OldSyntax_t0t1", primaryArm, "render").Kind == Kind.Gate)
        {
            string brighter = Replace(t0t1Src, "tex2D(Sampler1, uv) * 0.5", "tex2D(Sampler1, uv) * 1.0");
            builds.Add(CompileBuild(compiler, cfg, t0t1, primaryArm, "CONTROL-full-green.fx", brighter) with
            { IsControl = true, ControlCheck = "render", ControlName = "texture1 weight 1.0 instead of 0.5: render must fail" });
        }

        CaseDef brace = Cases.Single(c => c.Id == "MatrixBrace");
        if (Classify(cfg.Runtime, "MatrixBrace", "SD-MGFX", "render").Kind == Kind.Gate)
        {
            string braceSrc = File.ReadAllText(Path.Combine(companionDir, "MatrixBrace.fx"));
            string transposed = Replace(braceSrc, "float4x4 M = {0, 1, 0, 0,\n              0, 0, 0, 0,", "float4x4 M = {0, 0, 0, 0,\n              1, 0, 0, 0,");
            builds.Add(CompileBuild(compiler, cfg, brace, "SD-MGFX", "CONTROL-transposed-default.fx", transposed) with
            { IsControl = true, ControlCheck = "render", ControlName = "matrix default transposed in the source: render must fail" });
        }

        using var game = new ProbeGame(builds, cfg);
        game.Run();

        return Report(cfg, builds, game.Rows);
    }


    private static string Replace(string s, string from, string to)
    {
        if (!s.Contains(from, StringComparison.Ordinal))
            throw new InvalidOperationException($"control source no longer contains '{from}'");
        return s.Replace(from, to, StringComparison.Ordinal);
    }

    private static Build CompileBuild(EffectCompiler compiler, ProbeConfig cfg, CaseDef c, string arm, string path, string source)
    {
        EffectContainer container = arm.StartsWith("SD-KNIFX", StringComparison.Ordinal) ? EffectContainer.Knifx : EffectContainer.Mgfx;
        var r = compiler.CompileAsync(source, new CompilerOptions { Target = cfg.Target, Container = container, SourceFileName = path })
            .GetAwaiter().GetResult();
        if (r.IsFailure)
            return new Build(c, arm, null, "compile: " + string.Join(" | ", r.Error.Select(e => $"{e.Code}: {OneLine(e.Message)}")), []);
        byte[] bytes = r.Value.Data;
        string[] warnings = r.Value.Warnings.Select(w => $"{w.Code}: {OneLine(w.Message)}").ToArray();
        return new Build(c, arm, gd => new Effect(gd, bytes), null, warnings);
    }

    private static string OneLine(string s)
    {
        string t = s.Replace('\r', ' ').Replace('\n', ' ');
        return t.Length > 200 ? t[..200] + "..." : t;
    }

    private static Effect LoadXnb(GraphicsDevice gd, string root, string name)
    {
        var services = new GameServiceContainer();
        services.AddService(typeof(IGraphicsDeviceService), new DeviceService(gd));
        var content = new ContentManager(services, root);
        return content.Load<Effect>(name);
    }

    private sealed class DeviceService(GraphicsDevice gd) : IGraphicsDeviceService
    {
        public GraphicsDevice GraphicsDevice { get; } = gd;
#pragma warning disable CS0067
        public event EventHandler<EventArgs>? DeviceCreated;
        public event EventHandler<EventArgs>? DeviceDisposing;
        public event EventHandler<EventArgs>? DeviceReset;
        public event EventHandler<EventArgs>? DeviceResetting;
#pragma warning restore CS0067
    }

    // ----------------------------------------------------------------------------------------------
    // Report
    // ----------------------------------------------------------------------------------------------

    private static int Report(ProbeConfig cfg, List<Build> builds, List<Row> rows)
    {
        string L = $"[{cfg.Runtime}]";
        int expected = builds.Sum(b => b.IsControl ? 1 : b.Case.Checks.Length);
        int gatePass = 0, gateFail = 0, kfStill = 0, kfFlipped = 0, info = 0, ctlOk = 0, ctlMissed = 0;
        var lines = new List<string>();
        var legend = new Dictionary<string, string>(StringComparer.Ordinal);
        string Tag(Expectation e)
        {
            if (!legend.TryGetValue(e.Reason, out string? t))
                legend[e.Reason] = t = (e.Kind == Kind.KnownFail ? "K" : "I") + (legend.Count + 1);
            return t;
        }

        foreach (Row r in rows.Where(r => r.ControlName is null))
        {
            Expectation e = Classify(cfg.Runtime, r.Case, r.Arm, r.Check);
            string verdict;
            switch (e.Kind)
            {
                case Kind.Gate:
                    verdict = r.Pass ? "GATE        PASS" : "GATE        FAIL   <-- REGRESSION";
                    if (r.Pass) gatePass++; else gateFail++;
                    break;
                case Kind.KnownFail:
                    verdict = r.Pass ? "KNOWN-FAIL  PASS   <-- UNEXPECTED PASS: flip this row to GATE in KniEffectTestProbe.Classify" : "KNOWN-FAIL  fail";
                    if (r.Pass) kfFlipped++; else kfStill++;
                    break;
                default:
                    verdict = r.Pass ? "INFO        pass" : "INFO        fail";
                    info++;
                    break;
            }

            string why = e.Reason.Length > 0 ? $"[{Tag(e)}] " : "";
            lines.Add($"  {verdict,-18} {r.Case + "/" + r.Check,-36} {r.Arm,-12} {why}{r.Detail}");
        }

        foreach (Row r in rows.Where(r => r.ControlName is not null))
        {
            // A control is detected only if its check RAN and reported a value mismatch.
            bool detected = !r.Pass && !r.Detail.StartsWith("compile:", StringComparison.Ordinal) && !r.Detail.StartsWith("load:", StringComparison.Ordinal) && !r.Detail.StartsWith("threw", StringComparison.Ordinal);
            if (detected) ctlOk++; else ctlMissed++;
            lines.Add($"  {(detected ? "CONTROL     detected" : "CONTROL     MISSED  <-- the harness cannot see this defect"),-18} {r.Case + "/" + r.Check,-36} {r.Arm,-12} {r.ControlName}: {r.Detail}");
        }

        Console.WriteLine($"{L} KNI EffectTest results ({typeof(Game).Assembly.GetName().Name} {typeof(Game).Assembly.GetName().Version}):");
        foreach (string l in lines) Console.WriteLine(l);
        Console.WriteLine($"{L} reasons:");
        foreach ((string reason, string tag) in legend) Console.WriteLine($"  [{tag}] {reason}");

        bool complete = rows.Count == expected;
        Console.WriteLine();
        Console.WriteLine($"{L} gated {gatePass}/{gatePass + gateFail} pass; known-failing {kfStill} still failing, {kfFlipped} unexpectedly passing; informational {info}; controls {ctlOk}/{ctlOk + ctlMissed} detected; rows {rows.Count}/{expected}.");
        if (!complete) Console.WriteLine($"{L} FAIL: only {rows.Count} of {expected} rows ran");
        bool green = gateFail == 0 && kfFlipped == 0 && ctlMissed == 0 && complete;
        Console.WriteLine(green ? $"{L} GREEN" : $"{L} RED");
        return green ? 0 : 1;
    }

    // ----------------------------------------------------------------------------------------------
    // The Game: every build runs inside one Draw, as KNI's GraphicsDeviceTestFixtureBase does.
    // ----------------------------------------------------------------------------------------------

    private sealed class ProbeGame : Game
    {
        private readonly IReadOnlyList<Build> _builds;
        private readonly ProbeConfig _cfg;
        private bool _done;

        public List<Row> Rows { get; } = [];

        public ProbeGame(IReadOnlyList<Build> builds, ProbeConfig cfg)
        {
            _builds = builds;
            _cfg = cfg;
            var gdm = new GraphicsDeviceManager(this)
            {
                PreferredBackBufferWidth = 64,
                PreferredBackBufferHeight = 64,
                GraphicsProfile = GraphicsProfile.HiDef,
            };
            cfg.ConfigureDevice?.Invoke(gdm);
            Window.Title = "ShadowDusk KNI EffectTest gate (headless)";
        }

        protected override void Draw(GameTime gameTime)
        {
            if (_done) { Exit(); return; }
            _done = true;
            foreach (Build b in _builds)
            {
                var results = new Dictionary<string, (bool Pass, string Detail)>();
                string[] checks = b.IsControl ? [b.ControlCheck!] : b.Case.Checks;
                try
                {
                    if (b.Case.Source == Source.None) RunRuntimeOnly(b.Case, results);
                    else if (b.Load is null)
                    {
                        foreach (string c in checks) results[c] = (false, b.Error ?? "no build");
                    }
                    else RunShader(b, results);
                }
                catch (Exception ex)
                {
                    foreach (string c in checks)
                        if (!results.ContainsKey(c)) results[c] = (false, $"threw {ex.GetType().Name}: {OneLine(ex.Message)}");
                }

                try { GraphicsDevice.SetRenderTarget(null); } catch { /* best effort */ }
                foreach (string c in checks)
                {
                    (bool pass, string detail) = results.TryGetValue(c, out var v) ? v : (false, "check did not run");
                    Rows.Add(new Row(b.Case.Id, c, b.Arm, pass, detail, b.ControlName));
                }
            }

            Exit();
        }

        private void ResetDevice()
        {
            for (int i = 0; i < 16; i++)
            {
                GraphicsDevice.Textures[i] = null;
                GraphicsDevice.SamplerStates[i] = SamplerState.LinearWrap;
            }
        }

        private static string S(SamplerState? s) => s is null ? "null" : $"{s.Filter}/{s.AddressU}/{s.AddressV}";
        private static bool Eq(SamplerState? a, SamplerState? b) => a is not null && b is not null && a.Filter == b.Filter && a.AddressU == b.AddressU && a.AddressV == b.AddressV;
        private static string T(Texture? t) => t is null ? "null" : (string.IsNullOrEmpty(t.Name) ? "<unnamed>" : t.Name);
        private static string M(Matrix m) => $"[{m.M11},{m.M12},{m.M13},{m.M14} | {m.M21},{m.M22},{m.M23},{m.M24} | {m.M31},{m.M32},{m.M33},{m.M34} | {m.M41},{m.M42},{m.M43},{m.M44}]";
        private static bool Near(Color c, int r, int g, int b, int a) =>
            Math.Abs(c.R - r) <= 1 && Math.Abs(c.G - g) <= 1 && Math.Abs(c.B - b) <= 1 && Math.Abs(c.A - a) <= 1;

        /// <summary>Collects the failures of one check; empty means pass.</summary>
        private sealed class Check(string name, Dictionary<string, (bool, string)> into)
        {
            private readonly List<string> _fails = [];
            private readonly List<string> _notes = [];
            public void That(bool ok, string what) { if (!ok) _fails.Add(what); }
            public void Note(string n) => _notes.Add(n);
            public void Commit()
            {
                string notes = _notes.Count > 0 ? " (" + string.Join("; ", _notes) + ")" : "";
                into[name] = _fails.Count == 0 ? (true, PassToken + notes) : (false, string.Join("; ", _fails) + notes);
            }
        }

        private string State() =>
            $"Textures[0..2]=[{T(GraphicsDevice.Textures[0])},{T(GraphicsDevice.Textures[1])},{T(GraphicsDevice.Textures[2])}] Samplers[0..1]=[{S(GraphicsDevice.SamplerStates[0])},{S(GraphicsDevice.SamplerStates[1])}]";

        private void RunShader(Build b, Dictionary<string, (bool, string)> results)
        {
            GraphicsDevice d = GraphicsDevice;
            ResetDevice();
            if (b.Case.Id is "OldSyntax_x")
            {
                // The test sets these BEFORE loading the effect, and asserts the effect leaves them alone.
                d.SamplerStates[0] = SamplerState.LinearClamp;
                d.SamplerStates[1] = SamplerState.PointClamp;
            }

            Effect effect;
            try { effect = b.Load!(d); }
            catch (Exception ex)
            {
                foreach (string c in b.IsControl ? [b.ControlCheck!] : b.Case.Checks)
                    results[c] = (false, $"load: {ex.GetType().Name}: {OneLine(ex.Message)}");
                return;
            }

            var tex0 = new Texture2D(d, 1, 1, false, SurfaceFormat.Color) { Name = "texture0" };
            var tex1 = new Texture2D(d, 1, 1, false, SurfaceFormat.Color) { Name = "texture1" };
            tex0.SetData([new Color(0f, 0f, 1f, 1f)]);
            tex1.SetData([new Color(0f, 1f, 0f, 1f)]);
            try
            {
                switch (b.Case.Id)
                {
                    case "OldSyntax_x":
                    case "OldSyntax_s0s1":
                    {
                        bool s0s1 = b.Case.Id == "OldSyntax_s0s1";
                        var textures = new Check("textures", results);
                        var samplers = new Check("samplers", results);
                        textures.That(effect.Parameters.Count == 0, $"Parameters.Count={effect.Parameters.Count} [{string.Join(",", effect.Parameters.Select(p => p.Name))}] (XNA expects 0)");
                        d.Textures[0] = tex0;
                        d.Textures[1] = tex1;
                        effect.CurrentTechnique.Passes[0].Apply();
                        textures.That(d.Textures[0] == tex0, $"Textures[0]={T(d.Textures[0])} (want texture0)");
                        textures.That(d.Textures[1] == tex1, $"Textures[1]={T(d.Textures[1])} (want texture1)");
                        textures.That(d.Textures[2] is null, "Textures[2] not null");
                        SamplerState e0 = s0s1 ? SamplerState.PointWrap : SamplerState.LinearClamp;
                        samplers.That(Eq(d.SamplerStates[0], e0), $"SamplerStates[0]={S(d.SamplerStates[0])} (want {S(e0)})");
                        samplers.That(Eq(d.SamplerStates[1], SamplerState.PointClamp), $"SamplerStates[1]={S(d.SamplerStates[1])} (want {S(SamplerState.PointClamp)})");
                        samplers.That(d.SamplerStates[2] == SamplerState.LinearWrap, "SamplerStates[2] not LinearWrap");
                        textures.Commit();
                        samplers.Commit();
                        break;
                    }
                    case "OldSyntax_t0t1":
                    case "OldSyntax_t1t0":
                    case "OldSyntax_s0s1t0t1":
                    case "NewSyntax_t0t1":
                    case "NewSyntax_s0s1t0t1":
                    case "DeferredBasicEffect":
                    {
                        bool deferred = b.Case.Id == "DeferredBasicEffect";
                        bool pointStates = b.Case.Id.Contains("s0s1", StringComparison.Ordinal);
                        bool draw = b.Case.Id is "OldSyntax_t0t1" or "OldSyntax_t1t0";
                        var bind = new Check("bind", results);
                        SetTexture(effect, deferred ? "Diffuse" : "Texture0", tex0, bind);
                        SetTexture(effect, deferred ? "SpecularMap" : "Texture1", tex1, bind);
                        using var basic = new BasicEffect(d) { World = Matrix.Identity, View = Matrix.Identity, Projection = Matrix.Identity };
                        if (draw) basic.CurrentTechnique.Passes[0].Apply(); // the test applies BasicEffect's VS first
                        effect.CurrentTechnique.Passes[0].Apply();
                        bind.That(d.Textures[0] == tex0, $"Textures[0]={T(d.Textures[0])} (want texture0)");
                        bind.That(d.Textures[1] == tex1, $"Textures[1]={T(d.Textures[1])} (want texture1)");
                        bind.That(d.Textures[2] is null, $"Textures[2]={T(d.Textures[2])} (want null)");
                        SamplerState e0 = pointStates ? SamplerState.PointWrap : SamplerState.LinearWrap;
                        SamplerState e1 = pointStates ? SamplerState.PointClamp : SamplerState.LinearWrap;
                        bind.That(Eq(d.SamplerStates[0], e0), $"SamplerStates[0]={S(d.SamplerStates[0])} (want {S(e0)})");
                        bind.That(Eq(d.SamplerStates[1], e1), $"SamplerStates[1]={S(d.SamplerStates[1])} (want {S(e1)})");
                        bind.That(d.SamplerStates[2] == SamplerState.LinearWrap, "SamplerStates[2] not LinearWrap");
                        bind.Commit();
                        if (draw && _cfg.Runtime == "mg-vk")
                        {
                            // See InfoVulkanVsPsLinkage: the pairing is invalid Vulkan, so it is not issued.
                            results["render"] = (false, "not drawn (invalid VS/PS interface on Vulkan)");
                        }
                        else if (draw && (!b.IsControl || b.ControlCheck == "render"))
                        {
                            var render = new Check("render", results);
                            try
                            {
                                using var rt = new RenderTarget2D(d, 1, 1);
                                d.SetRenderTarget(rt);
                                d.Clear(Color.Black);
                                var v = new VertexPosition[4];
                                v[0].Position = new Vector3(-1, -1, 0);
                                v[1].Position = new Vector3(1, -1, 0);
                                v[2].Position = new Vector3(-1, 1, 0);
                                v[3].Position = new Vector3(1, 1, 0);
                                d.BlendState = BlendState.Opaque;
                                d.DepthStencilState = DepthStencilState.None;
                                d.RasterizerState = RasterizerState.CullNone;
                                d.DrawUserPrimitives(PrimitiveType.TriangleStrip, v, 0, 2);
                                d.SetRenderTarget(null);
                                var px = new Color[1];
                                rt.GetData(px);
                                render.That(Near(px[0], 0, 128, 255, 255), $"pixel ({px[0].R},{px[0].G},{px[0].B},{px[0].A}) want (0,128,255,255) +-1");
                                render.Note($"pixel ({px[0].R},{px[0].G},{px[0].B},{px[0].A})");
                                render.Commit();
                            }
                            catch (Exception ex)
                            {
                                results["render"] = (false, $"threw {ex.GetType().Name}: {OneLine(ex.Message)}");
                            }
                        }

                        break;
                    }
                    case "ComputeShader":
                        // KNI's test only compiles and constructs the effect; reaching here means it loaded.
                        results["load"] = (true, PassToken);
                        break;
                    case "OptimizedMatrix":
                    {
                        var dflt = new Check("default", results);
                        var round = new Check("roundtrip", results);
                        var warn = new Check("warnings", results);
                        EffectParameter? p = effect.Parameters["ColorMatrix"];
                        if (p is null) { dflt.That(false, "no ColorMatrix parameter"); round.That(false, "no ColorMatrix parameter"); }
                        else
                        {
                            effect.CurrentTechnique.Passes[0].Apply();
                            var xna = new Matrix(11, 12, 13, 14, 21, 22, 23, 24, 31, 32, 33, 34, 41, 42, 43, 44);
                            try
                            {
                                Matrix m = p.GetValueMatrix();
                                dflt.That(m == xna, $"GetValueMatrix() = {M(m)} (XNA: {M(xna)})");
                            }
                            catch (Exception ex) { dflt.That(false, $"GetValueMatrix() threw {ex.GetType().Name}"); }

                            var m3 = new Matrix(211, 212, 213, 214, 221, 222, 223, 224, 231, 232, 233, 234, 241, 242, 243, 244);
                            try
                            {
                                p.SetValue(m3);
                                Matrix m4 = p.GetValueMatrix();
                                round.That(m4 == m3, $"SetValue/GetValueMatrix round trip gave {M(m4)}");
                            }
                            catch (Exception ex) { round.That(false, $"SetValue/GetValueMatrix threw {ex.GetType().Name}: {OneLine(ex.Message)}"); }
                        }

                        // The initializer IS preserved, so DXC's 'initializer ignored' warning must not be passed on.
                        string? stale = b.Warnings.FirstOrDefault(w => w.Contains("initializer ignored", StringComparison.Ordinal));
                        warn.That(stale is null, $"stale warning: {stale}");
                        dflt.Commit();
                        round.Commit();
                        warn.Commit();
                        break;
                    }
                    case "OptimizedMatrixArray":
                    {
                        var round = new Check("roundtrip", results);
                        EffectParameter? p = effect.Parameters["ColorsMatrix"];
                        if (p is null) round.That(false, "no ColorsMatrix parameter");
                        else
                        {
                            effect.CurrentTechnique.Passes[0].Apply();
                            var a = new Matrix[]
                            {
                                new(11, 12, 13, 14, 21, 22, 23, 24, 31, 32, 33, 34, 41, 42, 43, 44),
                                new(211, 212, 213, 214, 221, 222, 223, 224, 231, 232, 233, 234, 241, 242, 243, 244),
                            };
                            try
                            {
                                p.SetValue(a);
                                Matrix[] r = p.GetValueMatrixArray(2);
                                round.That(r[0] == a[0] && r[1] == a[1], $"array round trip [0]={M(r[0])} [1]={M(r[1])}");
                            }
                            catch (Exception ex) { round.That(false, $"SetValue/GetValueMatrixArray threw {ex.GetType().Name}: {OneLine(ex.Message)}"); }
                        }

                        round.Commit();
                        break;
                    }
                    default:
                        RunCompanion(effect, results);
                        break;
                }
            }
            finally
            {
                effect.Dispose();
                tex0.Dispose();
                tex1.Dispose();
            }
        }

        private static void SetTexture(Effect e, string name, Texture2D t, Check into)
        {
            EffectParameter? p = e.Parameters[name];
            if (p is null) into.That(false, $"no parameter '{name}' [{string.Join(",", e.Parameters.Select(x => x.Name))}]");
            else p.SetValue(t);
        }

        /// <summary>The companion shaders: one SpriteBatch quad, the centre pixel must be the expected colour.</summary>
        private void RunCompanion(Effect effect, Dictionary<string, (bool, string)> results)
        {
            const int size = 16;
            GraphicsDevice d = GraphicsDevice;
            var render = new Check("render", results);
            var disposables = new List<IDisposable>();
            Color want = new(0, 255, 0, 255);
            try
            {
                using var sb = new SpriteBatch(d);
                var white = new Texture2D(d, 1, 1);
                disposables.Add(white);
                white.SetData([Color.White]);
                Texture2D Solid(Color c)
                {
                    var t = new Texture2D(d, 1, 1);
                    t.SetData([c]);
                    disposables.Add(t);
                    return t;
                }

                if (effect.Parameters["M"] is not null) want = new Color(0, 128, 0, 255);
                if (effect.Parameters["Arr"] is { } arr)
                {
                    var t = new Texture2D(d, 1, 1, false, SurfaceFormat.Color, 2);
                    disposables.Add(t);
                    t.SetData(0, 0, null, [new Color(255, 0, 0, 255)], 0, 1);
                    t.SetData(0, 1, null, [new Color(0, 255, 0, 255)], 0, 1);
                    arr.SetValue(t);
                }

                if (effect.Parameters["Tex1"] is { } t1) t1.SetValue(Solid(new Color(0, 255, 0, 255)));
                bool hasVs = false;
                if (effect.Parameters["MatrixTransform"] is { } mt && effect.Parameters["Tex"] is { } vt)
                {
                    hasVs = true;
                    mt.SetValue(Matrix.CreateOrthographicOffCenter(0, size, size, 0, 0, 1));
                    vt.SetValue(Solid(new Color(0, 255, 0, 255)));
                }

                // A fresh target per draw: after GetData, MonoGame DesktopVK leaves the image in a transfer
                // layout and reusing it as an attachment is a validation-layer error.
                using var rt = new RenderTarget2D(d, size, size, false, SurfaceFormat.Color, DepthFormat.None);
                d.SetRenderTarget(rt);
                d.Clear(Color.Transparent);
                if (!hasVs)
                {
                    // Prime SpriteBatch's own vertex shader (these effects are pixel-only).
                    sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
                    sb.Draw(white, new Rectangle(0, 0, size, size), Color.White);
                    sb.End();
                }

                d.Clear(new Color(255, 0, 255, 255));
                sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, null, null, effect);
                sb.Draw(white, new Rectangle(0, 0, size, size), Color.White);
                sb.End();
                d.SetRenderTarget(null);
                var px = new Color[size * size];
                rt.GetData(px);
                Color c = px[(size / 2) * size + size / 2];
                render.That(Near(c, want.R, want.G, want.B, want.A), $"centre ({c.R},{c.G},{c.B},{c.A}) want ({want.R},{want.G},{want.B},{want.A}) +-1");
                render.Note($"centre ({c.R},{c.G},{c.B},{c.A})");
                render.Commit();
            }
            catch (Exception ex)
            {
                results["render"] = (false, $"threw {ex.GetType().Name}: {OneLine(ex.Message)}");
            }
            finally
            {
                foreach (IDisposable x in disposables) x.Dispose();
            }
        }

        /// <summary>The five BasicEffect tests: the runtime's own effect, reported for completeness.</summary>
        private void RunRuntimeOnly(CaseDef c, Dictionary<string, (bool, string)> results)
        {
            GraphicsDevice d = GraphicsDevice;
            ResetDevice();
            using var texture = new Texture2D(d, 1, 1, false, SurfaceFormat.Color);
            using var effect = new BasicEffect(d) { TextureEnabled = true };
            switch (c.Id)
            {
                case "BasicEffect.ParamByIndex":
                {
                    var chk = new Check("value", results);
                    d.Textures[0] = texture;
                    effect.Texture = null;
                    effect.Parameters["DiffuseColor"]!.SetValue(Color.HotPink.ToVector4());
                    effect.Parameters["FogColor"]!.SetValue(Color.Honeydew.ToVector3());
                    // KNI's test: indexes 0/14 except on DESKTOPGL (1/2) and XNA (1/15).
                    bool gl = _cfg.Target == PlatformTarget.OpenGL;
                    int diffuse = gl ? 1 : 0, fog = gl ? 2 : 14;
                    chk.That(effect.Parameters.Count > fog && effect.Parameters[diffuse].GetValueVector4() == Color.HotPink.ToVector4(), $"Parameters[{diffuse}] is not DiffuseColor ({(effect.Parameters.Count > diffuse ? effect.Parameters[diffuse].Name : "?")})");
                    chk.That(effect.Parameters.Count > fog && effect.Parameters[fog].GetValueVector3() == Color.Honeydew.ToVector3(), $"Parameters[{fog}] is not FogColor ({(effect.Parameters.Count > fog ? effect.Parameters[fog].Name : "?")})");
                    chk.Commit();
                    break;
                }
                case "BasicEffect.SetTexture":
                case "BasicEffect.SetTextureTwice":
                {
                    var chk = new Check("bind", results);
                    d.Textures[0] = null;
                    effect.Texture = texture;
                    chk.That(d.Textures[0] is null, "Textures[0] set before Apply");
                    effect.CurrentTechnique.Passes[0].Apply();
                    chk.That(d.Textures[0] == texture, "Textures[0] not the effect texture after Apply");
                    if (c.Id == "BasicEffect.SetTextureTwice")
                    {
                        d.Textures[0] = null;
                        effect.CurrentTechnique.Passes[0].Apply();
                        chk.That(d.Textures[0] == texture, "Textures[0] not re-set by the second Apply");
                    }

                    chk.Commit();
                    break;
                }
                case "BasicEffect.SetTextureNull":
                case "BasicEffect.OverrideTexture":
                {
                    var chk = new Check("bind", results);
                    d.Textures[0] = texture;
                    if (c.Id == "BasicEffect.SetTextureNull") effect.Texture = null;
                    chk.That(d.Textures[0] == texture, "Textures[0] changed before Apply");
                    effect.CurrentTechnique.Passes[0].Apply();
                    chk.That(d.Textures[0] is null, $"Textures[0]={T(d.Textures[0])} after Apply (want null)");
                    chk.Commit();
                    break;
                }
            }
        }
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "ShadowDusk.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("repo root (ShadowDusk.slnx) not found above " + AppContext.BaseDirectory);
    }
}
