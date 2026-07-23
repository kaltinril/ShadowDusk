#nullable enable

using System.Runtime.InteropServices;
using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// A theory that runs the corpus sweep everywhere EXCEPT macOS, where compiling the whole
/// fixture corpus through DXC alongside the rest of the parallel integration suite takes the
/// test host down (`Test host process crashed`, reproducibly, ~11s into the assembly run and
/// at a DIFFERENT test each time — the signature of resource exhaustion, not a bad fixture).
/// Every case passes on macOS when it gets to run; the host dies underneath them.
///
/// <para><b>Why skipping here does not lose coverage.</b> ShadowDusk's output is proven
/// BYTE-IDENTICAL across hosts by <c>CrossHostByteIdentityTests</c> in this same suite, on all
/// three OSes. A structural gate over the emitted bytes therefore cannot find anything on macOS
/// that Linux and Windows do not already find — the bytes are the same bytes. The sweep still
/// runs in full on ubuntu-latest and windows-latest, and locally.</para>
///
/// <para>The macOS host crash under sustained DXC load is a real, separate issue and is tracked
/// in <c>plan/ISSUE-145-vulkan-vs-driven-and-legacy-sampler.md</c>; it is NOT a ShadowDusk
/// output defect (the same compiles succeed on every platform, and every case passes before the
/// host dies).</para>
/// </summary>
public sealed class CorpusSweepTheoryAttribute : TheoryAttribute
{
    public CorpusSweepTheoryAttribute()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Skip = "macOS test host crashes under the whole-corpus DXC sweep; output is proven " +
                   "byte-identical across hosts (CrossHostByteIdentityTests), so ubuntu + windows " +
                   "cover this. See plan/ISSUE-145-vulkan-vs-driven-and-legacy-sampler.md.";
    }
}

/// <summary>
/// <b>The corpus-wide Vulkan structural gate (issue #145, F7.1).</b> Compiles EVERY <c>.fx</c>
/// fixture in the corpus for the Vulkan target and asserts the structural invariants that make
/// a container loadable and drawable on MonoGame 3.8.5's DesktopVK — no GPU, no device, so it
/// runs anywhere <c>dotnet test</c> runs.
///
/// <para><b>Why this exists.</b> Both issue-#145 bugs were detectable from the emitted bytes
/// alone, and both slipped through because the Vulkan proof ran a hand-picked list of ten
/// PS-only, matrix-free, modern-syntax fixtures. Of the corpus's ~120 fixtures, ~80 use legacy
/// <c>tex2D</c> (bug 2's crash shape) and ~35 carry a matrix (bug 1). This gate covers all of
/// them at once.</para>
///
/// <para><b>No silent caps.</b> Every fixture is a test case, and a fixture that does NOT
/// compile is not skipped — it must still fail LOUDLY (a <c>ShaderError</c>, never an
/// exception or a process crash), which is its own regression guard. A fixture that DOES
/// compile must satisfy every structural invariant below. There is no exclusion list to
/// quietly grow.</para>
///
/// <para>The invariants, each traceable to real MonoGame runtime behaviour:</para>
/// <list type="bullet">
/// <item>Every image/sampler descriptor is <c>COMBINED_IMAGE_SAMPLER</c> at binding ≥ 32 —
/// <c>MGVK_UpdateDescriptors</c> recovers the slot as <c>binding - 32</c> and has no handler
/// for a standalone <c>SAMPLER</c>.</item>
/// <item>No two descriptors share a binding number (an invalid descriptor set layout).</item>
/// <item>A uniform buffer binds at 0 — the native pipeline indexes
/// <c>device-&gt;uniforms[stage + binding]</c>.</item>
/// <item>Matrices are HLSL column-major (SPIR-V <c>RowMajor</c>) — MonoGame uploads them
/// transposed for that convention.</item>
/// <item>The entry point is named <c>main</c>, and no <c>SPV_GOOGLE_*</c> extension ships.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "Vulkan")]
public sealed class VulkanCorpusStructuralTests
{
    private const int MinimumExpectedFixtures = 100;

    public static TheoryData<string> AllFixtures()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "fixtures", "shaders");
        var data = new TheoryData<string>();

        foreach (string path in Directory.EnumerateFiles(root, "*.fx", SearchOption.AllDirectories)
                                         .OrderBy(p => p, StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(root, path).Replace('\\', '/'));
        }

        return data;
    }

    [Fact]
    public void TheCorpusIsActuallyBeingEnumerated()
    {
        // Guards the guard: a broken glob or a missing content-copy would turn the theory
        // below into a silent no-op that still reports green.
        AllFixtures().Count.Should().BeGreaterThanOrEqualTo(MinimumExpectedFixtures,
            "the whole fixture corpus must reach this gate, not a hand-picked subset");
    }

    [CorpusSweepTheory]
    [MemberData(nameof(AllFixtures))]
    public async Task EveryFixture_EitherCompilesToAValidVulkanContainer_OrFailsLoudly(string relativePath)
    {
        string source = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "shaders", relativePath));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // A throw here IS the failure: an unsupported construct must surface as a ShaderError,
        // not an exception (and never as a native crash — see the FX0013 guard added for
        // vkd3d's SamplerComparisonState process abort).
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target         = PlatformTarget.Vulkan,
            SourceFileName = relativePath,
        }, cts.Token);

        if (result.IsFailure)
        {
            result.Error.Should().NotBeEmpty("a failed compile must carry at least one diagnostic");
            result.Error.Should().OnlyContain(e => !string.IsNullOrWhiteSpace(e.Code) &&
                                                   !string.IsNullOrWhiteSpace(e.Message),
                "every diagnostic must name a code and say what went wrong");
            return;
        }

        var reader = MgfxBlobReader.Parse(result.Value.Data);

        reader.ProfileId.Should().Be(80, "the Vulkan profile byte");
        reader.MgfxVersion.Should().Be(11, "Vulkan always writes the v11 shader-record shape");

        foreach (var shader in reader.Shaders)
        {
            string who = $"{relativePath} shader #{shader.Index} (isVertex={shader.IsVertex})";

            var vk = VulkanShaderCodeReader.Parse(shader.Bytecode);
            vk.SpirvMagicOk.Should().BeTrue($"{who} must wrap valid SPIR-V");

            vk.Bindings.Select(b => b.Binding).Should().OnlyHaveUniqueItems(
                $"{who}: two descriptor-set-layout bindings at one binding number is invalid");

            foreach (var binding in vk.Bindings)
            {
                switch (binding.DescriptorType)
                {
                    case 8: // UNIFORM_BUFFER_DYNAMIC
                        binding.Binding.Should().Be(0,
                            $"{who}: the native pipeline reads device->uniforms[stage + binding]");
                        break;

                    case 1: // COMBINED_IMAGE_SAMPLER
                        binding.Binding.Should().BeGreaterThanOrEqualTo(32,
                            $"{who}: the runtime recovers the texture slot as (binding - 32)");
                        break;

                    default:
                        Assert.Fail($"{who}: descriptor type {binding.DescriptorType} at binding " +
                                    $"{binding.Binding} — MonoGame's native descriptor writer only " +
                                    "handles UNIFORM_BUFFER_DYNAMIC, COMBINED_IMAGE_SAMPLER and " +
                                    "SAMPLED_IMAGE, and a separate SAMPLED_IMAGE/SAMPLER pair is " +
                                    "exactly the shape that access-violates (issue #145 bug 2)");
                        break;
                }
            }

            if (vk.Bindings.Any(b => b.DescriptorType == 1))
            {
                vk.SamplerSlots.Should().Be(vk.TextureSlots,
                    $"{who}: a combined descriptor occupies both slot masks, as mgfxc writes them");
            }

            SpirvDecorationScanner.EntryPointName(vk.Spirv).Should().Be("main",
                $"{who}: MonoGame's native Vulkan pipeline creation expects the entry point to be main");

            SpirvDecorationScanner.Extensions(vk.Spirv).Should().NotContain(
                e => e.StartsWith("SPV_GOOGLE", StringComparison.Ordinal),
                $"{who}: the shipped module must match mgfxc's reflect-free compile");

            // Only assert majorness when the module actually declares a matrix member.
            if (SpirvDecorationScanner.HasMatrixMember(vk.Spirv))
            {
                SpirvDecorationScanner.AllMatrixMembersAreSpirvRowMajor(vk.Spirv).Should().BeTrue(
                    $"{who}: DXC decorates an HLSL COLUMN-major matrix as SPIR-V RowMajor, which is " +
                    "the convention MonoGame uploads for; ColMajor means the shader reads every " +
                    "matrix transposed (issue #145 bug 1)");
            }
        }
    }
}
