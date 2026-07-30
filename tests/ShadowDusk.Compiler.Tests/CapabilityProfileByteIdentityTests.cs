#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Proves the Phase 35 auto-select dialect seam is byte-identical: selecting the proven
/// <see cref="CapabilityProfile.MonoGameGL_3_8_2"/> profile (the default GL contract) must not
/// change a single output byte versus no profile, and a profile is never honored on a target it
/// does not describe.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CapabilityProfileByteIdentityTests
{
    private static readonly string FixturesDir = FindFixturesDir();

    private static string ShaderPath(string fileName) => Path.Combine(FixturesDir, "shaders", fileName);

    private static string FindFixturesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir.Parent is not null)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "fixtures");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate tests/fixtures directory.");
    }

    private static async Task<byte[]> CompileBytesAsync(string fixture, CompilerOptions options)
    {
        string source = await File.ReadAllTextAsync(ShaderPath(fixture));
        var result = await new EffectCompiler().CompileAsync(source, options);
        result.IsSuccess.ShouldBeTrue(
            $"{fixture} should compile for {options.Target} (profile: {options.Profile?.ToString() ?? "none"})");
        return result.Value.Data;
    }

    private static Task<byte[]> CompileBytesAsync(string fixture, PlatformTarget target, CapabilityProfile? profile)
        => CompileBytesAsync(fixture, new CompilerOptions { Target = target, Profile = profile });

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGL_MonoGameGLProfile_IsByteIdenticalToNoProfile()
    {
        byte[] withoutProfile = await CompileBytesAsync("Minimal.fx", PlatformTarget.OpenGL, profile: null);
        byte[] withProfile = await CompileBytesAsync("Minimal.fx", PlatformTarget.OpenGL, CapabilityProfile.MonoGameGL_3_8_2);

        withProfile.ShouldBe(withoutProfile, customMessage: "MonoGameGL_3_8_2 is the proven default GL contract, so selecting it explicitly must emit identical bytes");
    }

    [Fact]
    [Trait("Platform", "DirectX")]
    public async Task Profile_ImpliesItsBackend_OverridingTarget()
    {
        // A CapabilityProfile fully specifies the output target, including the backend, so the
        // profile's GraphicsTarget wins over Target. A DirectX profile emits DirectX even when
        // Target is the default OpenGL, and a GL profile emits OpenGL even when Target is DirectX.
        byte[] dxViaTarget  = await CompileBytesAsync("Minimal.fx", PlatformTarget.DirectX, profile: null);
        byte[] dxViaProfile = await CompileBytesAsync("Minimal.fx", PlatformTarget.OpenGL, CapabilityProfile.MonoGameDX_SM5);
        dxViaProfile.ShouldBe(dxViaTarget, customMessage: "MonoGameDX_SM5 names the DirectX backend, so it emits DirectX output even when Target is OpenGL");

        byte[] glViaTarget  = await CompileBytesAsync("Minimal.fx", PlatformTarget.OpenGL, profile: null);
        byte[] glViaProfile = await CompileBytesAsync("Minimal.fx", PlatformTarget.DirectX, CapabilityProfile.MonoGameGL_3_8_2);
        glViaProfile.ShouldBe(glViaTarget, customMessage: "MonoGameGL_3_8_2 names the OpenGL backend, so it emits OpenGL output even when Target is DirectX");
    }

    [Fact]
    [Trait("Platform", "DirectX")]
    public async Task AutoDetectedProfile_CompilesToDetectedBackend_NoRegression()
    {
        // Locks the "don't break auto-detect" guarantee: a profile from RuntimeProfileDetector, set
        // as Profile alone (Target left at its OpenGL default), compiles to the DETECTED backend.
        CapabilityProfile detected = RuntimeProfileDetector.Recommend(DetectedRuntime.MonoGame, PlatformTarget.DirectX);
        byte[] viaDetectedProfile = await CompileBytesAsync("Minimal.fx", new CompilerOptions { Profile = detected });
        byte[] viaExplicitTarget  = await CompileBytesAsync("Minimal.fx", new CompilerOptions { Target = PlatformTarget.DirectX });

        viaDetectedProfile.ShouldBe(viaExplicitTarget, customMessage: "an auto-detected DirectX profile must compile to DirectX even though Target defaulted to OpenGL");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task KniProfile_SelectsKnifx_IsByteIdenticalToContainerKnifx()
    {
        // Seam 5: a profile selects the container, so KniGL_4_02 must emit exactly what the
        // low-level Container = Knifx option does.
        byte[] viaOption = await CompileBytesAsync("Minimal.fx", new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            Container = EffectContainer.Knifx,
        });
        byte[] viaProfile = await CompileBytesAsync("Minimal.fx", PlatformTarget.OpenGL, CapabilityProfile.KniGL_4_02);

        viaProfile.ShouldBe(viaOption, customMessage: "KniGL_4_02 names the KNIFX container, so it must emit identical bytes to Container = Knifx");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task MonoGameV11Profile_SelectsMgfxV11_IsByteIdenticalToMgfxVersion11()
    {
        // Seam 5: a profile selects the MGFX version, so MonoGameGL_3_8_5 must emit exactly what
        // the low-level MgfxVersion = 11 option does.
        byte[] viaOption = await CompileBytesAsync("Minimal.fx", new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            MgfxVersion = 11,
        });
        byte[] viaProfile = await CompileBytesAsync("Minimal.fx", PlatformTarget.OpenGL, CapabilityProfile.MonoGameGL_3_8_5);

        viaProfile.ShouldBe(viaOption, customMessage: "MonoGameGL_3_8_5 names MGFX v11, so it must emit identical bytes to MgfxVersion = 11");
    }

    // -------------------------------------------------------------------------
    // Phase 35 — __KNIFX__ macro fidelity. KNI's compiler always defines __KNIFX__; a
    // KNIFX-targeted compile must take the __KNIFX__ branch, the default MGFX output must not.
    // -------------------------------------------------------------------------

    private static string Ascii(byte[] data) =>
        System.Text.Encoding.ASCII.GetString(data.Select(b => (b >= 32 && b <= 126) ? b : (byte)' ').ToArray());

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task KnifxContainer_TakesKnifxBranch_DefaultMgfxDoesNot()
    {
        // ExKnifxMacro.fx: `#ifdef __KNIFX__` writes vec4(1,0,0,1) (KNIFX) else vec4(0,1,0,1).
        byte[] knifx = await CompileBytesAsync("examples/ExKnifxMacro.fx", new CompilerOptions
        {
            Target    = PlatformTarget.OpenGL,
            Container = EffectContainer.Knifx,
        });
        byte[] mgfx = await CompileBytesAsync("examples/ExKnifxMacro.fx", new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
        });

        string knifxGlsl = Ascii(knifx);
        string mgfxGlsl  = Ascii(mgfx);

        // KNIFX took the __KNIFX__ branch (the red constant); the universal MGFX output did not.
        knifxGlsl.ShouldContain("vec4(1.0, 0.0", Case.Sensitive, "KNI's compiler defines __KNIFX__, so the KNIFX container must take the __KNIFX__ branch");
        knifxGlsl.ShouldNotContain("vec4(0.0, 1.0", Case.Sensitive);
        mgfxGlsl.ShouldContain("vec4(0.0, 1.0", Case.Sensitive, "the default MGFX output is target-agnostic and must NOT define __KNIFX__");
        mgfxGlsl.ShouldNotContain("vec4(1.0, 0.0", Case.Sensitive);
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task KniProfile_TakesKnifxBranch_LikeContainerKnifx()
    {
        // The KniGL_4_02 capability profile (the --target-runtime kni-knifx path Vic would use)
        // selects KNIFX, so it must take the __KNIFX__ branch exactly like Container = Knifx.
        byte[] viaProfile = await CompileBytesAsync(
            "examples/ExKnifxMacro.fx", PlatformTarget.OpenGL, CapabilityProfile.KniGL_4_02);

        Ascii(viaProfile).ShouldContain("vec4(1.0, 0.0", Case.Sensitive, "KniGL_4_02 is a KNIFX (KNI) profile, so __KNIFX__ is defined");
    }
}
