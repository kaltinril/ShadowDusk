#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

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
        AllFixtures().Count.ShouldBeGreaterThanOrEqualTo(MinimumExpectedFixtures, customMessage: "the whole fixture corpus must reach this gate, not a hand-picked subset");
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public async Task EveryFixture_EitherCompilesToAValidVulkanContainer_OrFailsLoudly(string relativePath)
    {
        string source = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "shaders", relativePath));

        using var cts = new CancellationTokenSource(TestBudget.Compile);

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
            result.Error.ShouldNotBeEmpty("a failed compile must carry at least one diagnostic");
            result.Error.ShouldAllBe(e => !string.IsNullOrWhiteSpace(e.Code) &&
                                                   !string.IsNullOrWhiteSpace(e.Message), customMessage: "every diagnostic must name a code and say what went wrong");
            return;
        }

        var reader = MgfxBlobReader.Parse(result.Value.Data);

        reader.ProfileId.ShouldBe((byte)(80), customMessage: "the Vulkan profile byte");
        reader.MgfxVersion.ShouldBe((byte)(11), customMessage: "Vulkan always writes the v11 shader-record shape");

        foreach (var shader in reader.Shaders)
        {
            string who = $"{relativePath} shader #{shader.Index} (isVertex={shader.IsVertex})";

            var vk = VulkanShaderCodeReader.Parse(shader.Bytecode);
            vk.SpirvMagicOk.ShouldBeTrue($"{who} must wrap valid SPIR-V");

            vk.Bindings.Select(b => b.Binding).ShouldBeUnique(
                $"{who}: two descriptor-set-layout bindings at one binding number is invalid");

            foreach (var binding in vk.Bindings)
            {
                switch (binding.DescriptorType)
                {
                    case 8: // UNIFORM_BUFFER_DYNAMIC
                        binding.Binding.ShouldBe((uint)(0), customMessage: $"{who}: the native pipeline reads device->uniforms[stage + binding]");
                        break;

                    case 1: // COMBINED_IMAGE_SAMPLER
                        binding.Binding.ShouldBeGreaterThanOrEqualTo(32u, customMessage: $"{who}: the runtime recovers the texture slot as (binding - 32)");
                        break;

                    // A texture with no sampler (read with Load): mgfxc 3.8.5 writes exactly this,
                    // with a record of its own (sampler slot 255), and its build draws the texture
                    // in real DesktopVK (register-slots/RegisterLoadOnly.fx). The record invariant
                    // below requires that record. What stays forbidden is the standalone SAMPLER.
                    case 2: // SAMPLED_IMAGE
                        binding.Binding.ShouldBeGreaterThanOrEqualTo(32u, customMessage: $"{who}: the runtime recovers the texture slot as (binding - 32)");
                        reader.Samplers.ShouldContain(s => s.ShaderIndex == shader.Index && s.TextureSlot == binding.Binding - 32 && s.SamplerSlot == 255,
                            customMessage: $"{who}: a sampler-less texture at binding {binding.Binding} needs mgfxc's own record (slot {binding.Binding - 32}, sampler slot 255) or its parameter is never bound");
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
                // A combined descriptor occupies both slot masks, a sampler-less texture only the
                // texture mask, as mgfxc writes them.
                uint combinedMask = vk.Bindings.Where(b => b.DescriptorType == 1 && b.Binding is >= 32 and < 64)
                    .Aggregate(0u, (mask, b) => mask | (1u << (int)(b.Binding - 32)));
                vk.SamplerSlots.ShouldBe(combinedMask, customMessage: $"{who}: the sampler slot mask must name exactly the combined descriptors, as mgfxc writes it");
            }

            // A slot is (binding - 32) on both sides of the runtime: MGVK_UpdateDescriptors reads a
            // descriptor's image from textures[binding - 32], and EffectPass.Apply binds a texture
            // parameter to GraphicsDevice.Textures[record.TextureSlot]. mgfxc derives the record's
            // slot and the header's slot mask the same way. A slot that is any other number (the
            // texture's ordinal, which ShadowDusk once wrote) binds the parameter to a slot nothing
            // reads, so a lone register(t1) drew (0,0,0,0) in real DesktopVK.
            var imageSlots = vk.Bindings
                .Where(b => b.DescriptorType is 1 or 2 && b.Binding >= 32)
                .Select(b => (int)b.Binding - 32)
                .ToHashSet();
            uint expectedMask = imageSlots.Where(s => s < 32).Aggregate(0u, (mask, s) => mask | (1u << s));
            vk.TextureSlots.ShouldBe(expectedMask, customMessage: $"{who}: the texture slot mask must name exactly the (binding - 32) slots of its image descriptors");
            foreach (var record in reader.Samplers.Where(s => s.ShaderIndex == shader.Index))
            {
                imageSlots.ShouldContain(record.TextureSlot,
                    customMessage: $"{who}: sampler record '{record.Name}' binds its texture to slot {record.TextureSlot}, which no descriptor reads (slots read: {string.Join(", ", imageSlots.Order())})");
            }

            // DesktopVK binds ONE descriptor set per stage: the vertex shader's resources from set
            // 0, the pixel shader's from set 1. A resource in any other set is read from a
            // descriptor the runtime never writes (an explicit `register(t1, space0)` in a pixel
            // shader drew (0,0,0,0), in mgfxc's build too).
            var reflected = new ShadowDusk.Core.Reflection.SpirvReflector().Reflect(vk.Spirv);
            reflected.IsSuccess.ShouldBeTrue($"{who}: the shipped SPIR-V must reflect");
            int set = shader.IsVertex ? 0 : 1;
            foreach (var (kind, name, actual) in reflected.Value.Textures.Select(t => ("texture", t.Name, t.RawDescriptorSet))
                         .Concat(reflected.Value.Samplers.Select(s => ("sampler", s.Name, s.RawDescriptorSet)))
                         .Concat(reflected.Value.ConstantBuffers.Select(c => ("constant buffer", c.Name, c.RawDescriptorSet))))
            {
                actual.ShouldBe(set, customMessage: $"{who}: {kind} '{name}' must be in descriptor set {set}, the only one DesktopVK binds for this stage");
            }

            SpirvDecorationScanner.EntryPointName(vk.Spirv).ShouldBe("main", customMessage: $"{who}: MonoGame's native Vulkan pipeline creation expects the entry point to be main");

            SpirvDecorationScanner.Extensions(vk.Spirv).ShouldNotContain(
                e => e.StartsWith("SPV_GOOGLE", StringComparison.Ordinal), $"{who}: the shipped module must match mgfxc's reflect-free compile");

            // Only assert majorness when the module actually declares a matrix member.
            if (SpirvDecorationScanner.HasMatrixMember(vk.Spirv))
            {
                SpirvDecorationScanner.AllMatrixMembersAreSpirvRowMajor(vk.Spirv).ShouldBeTrue(
                    $"{who}: DXC decorates an HLSL COLUMN-major matrix as SPIR-V RowMajor, which is " +
                    "the convention MonoGame uploads for; ColMajor means the shader reads every " +
                    "matrix transposed (issue #145 bug 1)");
            }
        }
    }
}
