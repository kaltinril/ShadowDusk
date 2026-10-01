#nullable enable

using ShadowDusk.Compiler.Slang;
using ShadowDusk.Core;

namespace ShadowDusk.Slang;

/// <summary>
/// Phase 66 A5, Band 2 part B (OQ2, Phase 61 §6 / Phase 65 §1): rejects a Slang entry point
/// that uses an HLSL Shader Model 6 wave/quad intrinsic when the caller's
/// <see cref="PlatformTarget"/> can never represent SM6 HLSL through ShadowDusk's real
/// pipeline — a Slang construct that compiles (real slangc accepts it) but has nowhere to
/// land, the same "band 2" class <c>SD0602</c> already rejects for compute/mesh entry points.
///
/// <para><b>Why a static source scan, not a downstream catch-and-wrap</b> (measured, Phase 66
/// A5): a Wave/Quad intrinsic call is syntactically just an ordinary function call — slangc's
/// <c>-target hlsl</c> emission preserves the identifier verbatim (Slang's own Wave intrinsics
/// share HLSL's exact names for interop) on every target with no target-specific spelling
/// difference, so there is no way to catch this from the emitted HLSL's SHAPE either; the only
/// difference shows up downstream, in each backend's own diagnostic, and those diagnostics
/// disagree with each other AND are phrased around the backend's own internals rather than the
/// Slang author's target: DXC accepts the call (DXC's grammar is always SM6) and only refuses at
/// SPIR-V generation once ShadowDusk's own OpenGL DXC invocation lacks the Vulkan 1.1 capability
/// the operation needs ("error: Vulkan 1.1 is required for Wave Operation but not permitted to
/// use" — confusing wording for an OpenGL target, since ShadowDusk's use of Vulkan/SPIR-V as an
/// OpenGL intermediate is an implementation detail no Slang author should need to know);
/// vkd3d-shader's SM&lt;=3/5.1 HLSL frontend (DirectX's default backend, and FNA's only one) has
/// never heard of the intrinsic at all ("E5005: Function 'WaveActiveSum' is not defined" — reads
/// like a typo, not a target-capability gap). A source-level scan for the finite, documented HLSL
/// SM6 Wave/Quad intrinsics vocabulary — the same "closed language keyword set" shape
/// <c>ShadowDusk.Compiler.Slang.SlangFrontend</c>'s <c>SD0600</c> scan already uses for
/// <c>import</c>/<c>module</c>/<c>extension</c>/<c>associatedtype</c>/<c>__generic</c> — gives
/// ONE consistent, correctly-targeted diagnostic instead, and is cheaper (no process spawn for
/// an entry point that is going to be rejected regardless).</para>
///
/// <para><b>Scope, stated plainly:</b> this covers the ONE construct class Phase 65 §1's OQ2
/// caveat named and Phase 66 A5 then measured concretely (wave/quad subgroup intrinsics).
/// Raytracing and mesh/amplification SM6 entry-point shapes never reach this guard at all —
/// <see cref="SlangEntryScanner"/>'s <c>SD0602</c> already rejects any non-vertex/fragment
/// <c>[shader(...)]</c> stage before compilation starts. Rarer standalone SM6-only resource
/// forms callable from an ordinary vertex/pixel body (a templated <c>ResourceDescriptorHeap</c>
/// index, a 64-bit interlocked op, …) are NOT enumerated here — no construct in either the
/// shipped 17-shader corpus or Phase 65's broadened 33-shader sweep exercised one, so building a
/// list for them now would be guessing at their exact diagnostic shape rather than measuring it;
/// they fall through to whatever the downstream compiler reports, unmodified (Band 3 of the A5
/// table — a real compile failure, never silently dropped, just not specially re-labeled).</para>
/// </summary>
internal static class SlangSm6ConstructGuard
{
    // The closed HLSL SM6 wave/quad vocabulary now lives in ShadowDusk.Core's
    // WaveQuadIntrinsics, shared with the .fx route's Vulkan rejection (SD0218), so both routes
    // recognise exactly the same identifiers.
    internal static IReadOnlyList<string> WaveAndQuadIntrinsics => WaveQuadIntrinsics.Names;

    /// <summary>
    /// Returns the first Wave/Quad SM6 intrinsic name found as a whole identifier in
    /// <paramref name="slangSource"/>, plus its 1-based line, or <c>null</c> when none appears.
    /// A courtesy scan (matches <c>SD0600</c>'s own caveat): it can miss an intrinsic reached
    /// only through a macro expansion or an <c>import</c>ed module slangc itself resolves, since
    /// this runs on the raw source text before slangc sees it.
    /// </summary>
    public static (string Construct, int Line)? FindConstruct(string slangSource)
    {
        (string Name, int Line)? hit = WaveQuadIntrinsics.FindFirst(slangSource);
        return hit is null ? null : (hit.Value.Name, hit.Value.Line);
    }

    /// <summary>
    /// Targets whose real ShadowDusk backend is architecturally capped below Shader Model 6
    /// (measured, Phase 66 A5): OpenGL compiles through DXC at a fixed SM5 profile
    /// (<see cref="PlatformTarget"/>'s OpenGL case in <c>DxcFlagBuilder</c>, <c>vs_5_0</c>/
    /// <c>ps_5_0</c>); DirectX (DX11) ships SM5 DXBC — the container format itself has no SM6
    /// representation, with or without the Windows-only <c>DxbcBackend.D3DCompiler</c> opt-in;
    /// FNA ships SM&lt;=3 <c>fx_2_0</c>. These three can NEVER hold an SM6-only construct, by
    /// format, regardless of any future flag fix.
    ///
    /// <para>Vulkan and DirectX12 both compile through DXC at <c>vs_6_0</c>/<c>ps_6_0</c> and
    /// CAN represent SM6 HLSL, so they are excluded here. Vulkan still rejects wave/quad
    /// intrinsics, but for a runtime reason, not a format one: MonoGame's DesktopVK creates a
    /// Vulkan 1.0 instance with no subgroup support (issue #229, measured under the Khronos
    /// validation layer). That is <c>SD0218</c>, shared with the <c>.fx</c> route through
    /// <see cref="WaveQuadIntrinsics"/>, not this guard's <c>SD0624</c>.</para>
    /// </summary>
    public static bool IsArchitecturallyBelowSm6(PlatformTarget target) =>
        WaveQuadIntrinsics.IsBelowSm6Target(target);
}
