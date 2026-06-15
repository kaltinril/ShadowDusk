#nullable enable

using FluentAssertions;
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
        result.IsSuccess.Should().BeTrue(
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

        withProfile.Should().Equal(withoutProfile,
            "MonoGameGL_3_8_2 is the proven default GL contract, so selecting it explicitly must emit identical bytes");
    }

    [Fact]
    [Trait("Platform", "DirectX")]
    public async Task DirectX_GLProfileIsIgnored_IsByteIdenticalToNoProfile()
    {
        // The Target == OpenGL guard means a GL-dialect profile can never force the GL rewrite onto
        // a DirectX compile: a mismatched profile is inert, not corrupting.
        byte[] withoutProfile = await CompileBytesAsync("Minimal.fx", PlatformTarget.DirectX, profile: null);
        byte[] withGlProfile = await CompileBytesAsync("Minimal.fx", PlatformTarget.DirectX, CapabilityProfile.MonoGameGL_3_8_2);

        withGlProfile.Should().Equal(withoutProfile,
            "a GL profile must not change DirectX output (the dialect gate is guarded by Target == OpenGL)");
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

        viaProfile.Should().Equal(viaOption,
            "KniGL_4_02 names the KNIFX container, so it must emit identical bytes to Container = Knifx");
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

        viaProfile.Should().Equal(viaOption,
            "MonoGameGL_3_8_5 names MGFX v11, so it must emit identical bytes to MgfxVersion = 11");
    }
}
