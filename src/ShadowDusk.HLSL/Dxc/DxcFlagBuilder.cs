#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.HLSL.Dxc;

internal static class DxcFlagBuilder
{
    public static IReadOnlyList<string> Build(
        PlatformTarget platform,
        ShaderStage stage,
        string entryPoint,
        IReadOnlyList<(string Name, string? Value)> macros,
        DxcCompileOptions? options = null)
    {
        options ??= new DxcCompileOptions();

        var args = new List<string>();

        args.Add("-E");
        args.Add(entryPoint);

        string profile;
        string[] platformFlags;

        switch ((platform, stage))
        {
            case (PlatformTarget.OpenGL, ShaderStage.Vertex):
                profile = ShaderProfiles.Sm5Vertex;
                platformFlags = new[] { "-spirv", "-fvk-use-dx-layout", "-fvk-use-dx-position-w" };
                break;

            case (PlatformTarget.OpenGL, ShaderStage.Pixel):
                profile = ShaderProfiles.Sm5Pixel;
                platformFlags = new[] { "-spirv", "-fvk-use-dx-layout", "-auto-binding-space", "1" };
                break;

            // -fvk-bind-globals pins the implicit "$Globals" cbuffer (DXC's auto-generated
            // home for loose top-level globals like "float3 _sepiaTone;") to a fixed
            // binding, matching whatever auto-binding-space this stage's other auto-bound
            // resources use. Without it, DXC auto-numbers $Globals wherever it lands next
            // in declaration order (e.g. binding 1 instead of 0 depending on source
            // layout), and MonoGame's native Vulkan pipeline crashes (AccessViolationException
            // in GraphicsDevice_DrawIndexed) the moment that binding isn't 0 - confirmed via
            // a minimal repro (Sepia.fx, 2026-07-18): forcing the cbuffer to an explicit
            // register(b0) fixed the crash outright, isolating the auto-numbered raw binding
            // value (not the descriptor shape, already fixed separately) as the cause.
            // NOTE on -fspv-reflect: mgfxc compiles TWICE — once with it (to dump a reflection
            // listing it parses) and once WITHOUT it for the bytecode it actually ships, because
            // (its comment) the flag "forces Google VK extensions into the binary". ShadowDusk
            // reflects from the SPIR-V itself and reads only core decorations (Binding,
            // DescriptorSet, Offset, MatrixStride, RowMajor/ColMajor) plus OpName debug names —
            // none of which come from SPV_GOOGLE_hlsl_functionality1 — so it simply never asks
            // for the flag and ships the same clean module mgfxc does, in one compile instead of
            // two (issue #145, divergence S3; resolves Phase 32's open item 5).
            case (PlatformTarget.Vulkan, ShaderStage.Vertex):
                profile = "vs_6_0";
                platformFlags = new[]
                {
                    "-spirv", "-fvk-use-dx-layout", "-fvk-invert-y", "-fvk-use-dx-position-w",
                    "-fvk-t-shift", "32", "all", "-fvk-s-shift", "32", "all",
                    "-fvk-bind-globals", "0", "0",
                };
                break;

            case (PlatformTarget.Vulkan, ShaderStage.Pixel):
                profile = "ps_6_0";
                platformFlags = new[]
                {
                    "-spirv", "-fvk-use-dx-layout", "-auto-binding-space", "1",
                    "-fvk-t-shift", "32", "all", "-fvk-s-shift", "32", "all",
                    "-fvk-bind-globals", "0", "1",
                };
                break;

            // DXC does not support SM5 DXBC output; minimum profile is SM6 (DXIL).
            case (PlatformTarget.DirectX, ShaderStage.Vertex):
                profile = "vs_6_0";
                platformFlags = Array.Empty<string>();
                break;

            case (PlatformTarget.DirectX, ShaderStage.Pixel):
                profile = "ps_6_0";
                platformFlags = Array.Empty<string>();
                break;

            // DirectX 12: same SM6 DXIL compile as the DirectX case above (no -spirv), but this
            // one is the SHIPPED bytecode (wrapped by DirectX12ShaderCodeWrapper), not a
            // reflection-only side path. A prior pass here added mgfxc's rootsig-embedding
            // flags (-force-rootsig-ver / -rootsig-define / -D_MG_ROOT_SIGNATURE) on the
            // THEORY that MonoGame's WindowsDX12 PSO creation validates a shader's embedded
            // RTS0 against its own fixed root signature. That theory is falsified by directly
            // decoding a REAL mgfxc-compiled DirectX_12 golden (Phase 54 follow-up,
            // 2026-07-23): its DXIL container carries NO RTS0 part at all (6 parts: SFI0/
            // ISG1/OSG1/PSV0/HASH/DXIL) — mgfxc's own DirectX12ShaderProfile passes those
            // flags too, but since no `.fx` source (mgfxc's or ours) carries a
            // `[RootSignature(_MG_ROOT_SIGNATURE)]` attribute referencing the macro, DXC never
            // actually attaches one; the flags are vestigial on the reference compiler's own
            // command line. Adding them here made ShadowDusk's output diverge from the real
            // golden (ours carried a spurious RTS0 + STAT part the reference never ships) —
            // no -spirv, no rootsig flags, matches the real compiled reference exactly.
            case (PlatformTarget.DirectX12, ShaderStage.Vertex):
                profile = "vs_6_0";
                platformFlags = Array.Empty<string>();
                break;

            case (PlatformTarget.DirectX12, ShaderStage.Pixel):
                profile = "ps_6_0";
                platformFlags = Array.Empty<string>();
                break;

            // Metal goes through SPIR-V so use the same profile/flags as OpenGL
            case (PlatformTarget.Metal, ShaderStage.Vertex):
                profile = ShaderProfiles.Sm5Vertex;
                platformFlags = new[] { "-spirv", "-fvk-use-dx-layout", "-fvk-use-dx-position-w" };
                break;

            case (PlatformTarget.Metal, ShaderStage.Pixel):
                profile = ShaderProfiles.Sm5Pixel;
                platformFlags = new[] { "-spirv", "-fvk-use-dx-layout", "-auto-binding-space", "1" };
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(platform), $"Unsupported platform/stage: {platform}/{stage}");
        }

        args.Add("-T");
        args.Add(profile);

        args.AddRange(platformFlags);

        // Matrix packing. -Zpr (row-major) is REQUIRED by the OpenGL path — MonoGameGlslRewriter's
        // BuildUploadedMat4 reconstructs the uploaded registers assuming it (the issue-#70 fix) —
        // and irrelevant to DirectX (that target compiles through d3dcompiler_47 with
        // ShaderFlags.PackMatrixColumnMajor, not through DXC at all).
        //
        // Vulkan must NOT have it (issue #145). Real mgfxc's Vulkan DXC command line carries no
        // -Zpr, so its SPIR-V uses HLSL's column-major default; MonoGame's runtime uploads a
        // Matrix parameter to match that convention and says so explicitly — EffectParameter
        // .SetValue(Matrix) "HLSL expects matrices to be transposed by default" (it writes
        // M11,M21,M31,M41,…) and ConstantBuffer.SetParameter "HLSL assumes matrices are
        // column-major, whereas in-memory we use row-major. TODO: HLSL can be told to use
        // row-major. We should handle that too." That TODO is exactly the case -Zpr created here:
        // the runtime has no idea the shader was built row-major, so every matrix arrived
        // TRANSPOSED and any VS-driven Vulkan effect threw its geometry out of clip space and
        // rendered nothing. Verified against real mgfxc 3.8.5 output on the same shader: mgfxc
        // decorates the cbuffer matrix SPIR-V RowMajor (= HLSL column-major; DXC inverts the term
        // because SPIR-V stores matrices as column vectors), ShadowDusk decorated it ColMajor.
        // Dropping the flag here makes the two match with no other container change.
        //
        // DirectX12 must ALSO not have it — the reference compiler's DirectX12ShaderProfile
        // passes "/Zpc" (column-major) explicitly (Phase 54 research), the same convention
        // Vulkan uses by omission.
        if (platform != PlatformTarget.Vulkan && platform != PlatformTarget.DirectX12)
            args.Add("-Zpr");

        if (!options.AllowWarnings)
            args.Add("-WX");

        if (options.EmbedDebugInfo)
        {
            args.Add("-Zi");
            args.Add("-Qembed_debug");
        }

        foreach ((string name, string? value) in macros)
        {
            if (value is null)
                args.Add($"-D{name}");
            else
                args.Add($"-D{name}={value}");
        }

        return args;
    }

    /// <summary>
    /// Builds the DXC argument list for a preprocess-only invocation (<c>-P</c>): expand
    /// includes/macros/conditionals and emit the flat HLSL text. Deliberately carries NO
    /// <c>-E</c>/<c>-T</c> (no entry/profile — preprocessing is stage-agnostic) and NO
    /// <c>-WX</c> (warnings-as-errors must not fail a pure expansion). Only the macro
    /// defines are forwarded so the right <c>#if</c> branch (e.g. <c>SM4</c>) is taken.
    /// </summary>
    public static IReadOnlyList<string> BuildPreprocess(
        IReadOnlyList<(string Name, string? Value)> macros)
    {
        var args = new List<string> { "-P" };

        foreach ((string name, string? value) in macros)
        {
            if (value is null)
                args.Add($"-D{name}");
            else
                args.Add($"-D{name}={value}");
        }

        return args;
    }
}
