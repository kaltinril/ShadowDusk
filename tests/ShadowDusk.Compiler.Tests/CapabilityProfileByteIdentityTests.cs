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

    private static async Task<byte[]> CompileBytesAsync(string fixture, PlatformTarget target, CapabilityProfile? profile)
    {
        string source = await File.ReadAllTextAsync(ShaderPath(fixture));
        var result = await new EffectCompiler().CompileAsync(
            source, new CompilerOptions { Target = target, Profile = profile });
        result.IsSuccess.Should().BeTrue($"{fixture} should compile for {target} (profile: {profile?.ToString() ?? "none"})");
        return result.Value.Data;
    }

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
}
