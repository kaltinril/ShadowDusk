#nullable enable

using FluentAssertions;
using FluentAssertions.Execution;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Tests.D3DCompiler;
using ShadowDusk.HLSL.Tests.Vkd3d;
using ShadowDusk.HLSL.Vkd3d;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Reflection;

/// <summary>
/// THE load-bearing evidence for Phase 18 Track A: the pure-managed
/// <see cref="RdefReader"/> must produce a <see cref="ReflectedEffect"/> deeply equal
/// to d3dcompiler_47's <c>D3DReflect</c> (<see cref="D3DReflectOracle"/> — the exact
/// code the product shipped with before Track A), for the DXBC of BOTH backends
/// (d3dcompiler_47 and vkd3d-shader). Oracle parity is the bar, not "looks right":
/// any divergence is a managed-reader bug, because reflection feeds the cbuffer layout
/// and parameter list baked into the <c>.mgfx</c>. Windows-only by nature (the oracle
/// P/Invokes d3dcompiler_47); the managed reader itself is OS-independent and covered
/// everywhere by the pure <c>RdefReaderTests</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DxbcReflectionParityTests
{
    /// <summary>
    /// The parity corpus: representative MonoGame-style shader shapes — minimal,
    /// textured (incl. the empty-$Globals drop), cbuffer-heavy (scalars of every mapped
    /// type, vectors, matrices, arrays, matrix arrays), struct cbuffers (incl. nesting
    /// and struct arrays), a VS+PS pair, VS system-value inputs, SV_Depth output
    /// (the register=-1 + system-value fix-up case), and cube/volume textures.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Source, string Entry, ShaderStage Stage)> Corpus =
    [
        ("MinimalPs", """
            struct PSInput { float4 Position : SV_POSITION; float4 Color : COLOR0; };
            float4 MainPS(PSInput input) : SV_TARGET { return input.Color; }
            """, "MainPS", ShaderStage.Pixel),

        ("TexturedPs", """
            Texture2D SpriteTexture;
            SamplerState SpriteTextureSampler;
            float4 TintColor;
            struct PSInput { float4 Position : SV_POSITION; float2 Tex : TEXCOORD0; };
            float4 MainPS(PSInput input) : SV_TARGET
            {
                return SpriteTexture.Sample(SpriteTextureSampler, input.Tex) * TintColor;
            }
            """, "MainPS", ShaderStage.Pixel),

        // Texture-only: $Globals is empty, exercising the empty-cbuffer drop.
        ("TextureOnlyPs", """
            Texture2D SpriteTexture;
            SamplerState SpriteTextureSampler;
            struct PSInput { float4 Position : SV_POSITION; float2 Tex : TEXCOORD0; };
            float4 MainPS(PSInput input) : SV_TARGET
            {
                return SpriteTexture.Sample(SpriteTextureSampler, input.Tex);
            }
            """, "MainPS", ShaderStage.Pixel),

        ("CbufferHeavyPs", """
            cbuffer Params : register(b1)
            {
                float    Scale;
                bool     Enabled;
                int      Count;
                uint     Bits;
                float3   Direction;
                float4   Color;
                float4x4 World;
                float3x3 Normals;
                float    Weights[4];
                float4   Offsets[3];
                float4x3 Bones[2];
            };
            float4 MainPS() : SV_TARGET
            {
                float4 c = mul(float4(Direction * Scale, Weights[2]), World) * Color;
                c.xyz = mul(c.xyz, Normals) + Offsets[1].xyz + mul(float4(c.xyz, 1), Bones[1]);
                return Enabled ? c * Count : c * Bits;
            }
            """, "MainPS", ShaderStage.Pixel),

        ("StructCbufferPs", """
            struct Attenuation { float Constant; float Linear; };
            struct DirectionalLight
            {
                float3      Dir;
                float3      Color;
                float       Intensity;
                Attenuation Atten;
            };
            cbuffer LightParams : register(b0)
            {
                DirectionalLight Light;
                DirectionalLight Rim[2];
            };
            float4 MainPS() : SV_TARGET
            {
                return float4(
                    Light.Color * Light.Intensity * Light.Atten.Linear
                        + Rim[1].Dir * Rim[0].Atten.Constant,
                    1.0);
            }
            """, "MainPS", ShaderStage.Pixel),

        ("VsDrivenVs", """
            cbuffer Transforms
            {
                float4x4 WorldViewProjection;
                float4   DiffuseColor;
            };
            struct VSInput  { float3 Position : POSITION; float3 Normal : NORMAL; float2 Tex : TEXCOORD0; };
            struct VSOutput { float4 Position : SV_POSITION; float4 Color : COLOR0; float2 Tex : TEXCOORD0; };
            VSOutput MainVS(VSInput input)
            {
                VSOutput o;
                o.Position = mul(float4(input.Position, 1), WorldViewProjection);
                o.Color    = DiffuseColor * saturate(dot(input.Normal, float3(0, 1, 0)));
                o.Tex      = input.Tex;
                return o;
            }
            """, "MainVS", ShaderStage.Vertex),

        // SV_VertexID input: UInt32 component type + a non-Undefined input system value.
        ("VsSystemValuesVs", """
            float4 MainVS(uint id : SV_VertexID) : SV_POSITION
            {
                return float4(id * 1.0f, 0, 0, 1);
            }
            """, "MainVS", ShaderStage.Vertex),

        // SV_Depth: register 0xFFFFFFFF (-1) + the name-based system-value fix-up
        // (the OSGN chunk stores 0; D3DReflect reports Target/Depth).
        ("DepthOutputPs", """
            struct PSInput { float4 Position : SV_POSITION; };
            float4 MainPS(PSInput input, out float depth : SV_Depth) : SV_TARGET
            {
                depth = 0.5;
                return float4(1, 0, 0, 1);
            }
            """, "MainPS", ShaderStage.Pixel),

        ("CubeVolumePs", """
            TextureCube  EnvMap  : register(t2);
            Texture3D    Volume  : register(t5);
            SamplerState EnvSamp : register(s1);
            SamplerState VolSamp : register(s3);
            float3 LookupDir;
            float4 MainPS() : SV_TARGET
            {
                return EnvMap.SampleLevel(EnvSamp, LookupDir, 0)
                     + Volume.SampleLevel(VolSamp, LookupDir, 0);
            }
            """, "MainPS", ShaderStage.Pixel),
    ];

    [WindowsFact]
    public async Task ManagedReader_MatchesD3DReflect_OnD3DCompilerDxbc()
    {
        var compiler = new D3DCompilerShaderCompiler();

        foreach ((string name, string source, string entry, ShaderStage stage) in Corpus)
        {
            var compiled = await compiler.CompileAsync(new D3DCompileRequest
            {
                HlslSource     = source,
                SourceFileName = $"{name}.hlsl",
                EntryPoint     = entry,
                Stage          = stage,
                AllowWarnings  = true,
            });

            compiled.IsSuccess.Should().BeTrue(
                because: compiled.IsFailure
                    ? $"{name}: {compiled.Error.Message}"
                    : $"{name} must compile via d3dcompiler_47");

            AssertParity(name, compiled.Value.Bytes);
        }
    }

    [Vkd3dFact(requiresD3DReflect: true)]
    public async Task ManagedReader_MatchesD3DReflect_OnVkd3dDxbc()
    {
        var compiler = new Vkd3dShaderCompiler();

        foreach ((string name, string source, string entry, ShaderStage stage) in Corpus)
        {
            var compiled = await compiler.CompileAsync(new D3DCompileRequest
            {
                HlslSource     = source,
                SourceFileName = $"{name}.hlsl",
                EntryPoint     = entry,
                Stage          = stage,
                AllowWarnings  = true,
            });

            compiled.IsSuccess.Should().BeTrue(
                because: compiled.IsFailure
                    ? $"{name}: {compiled.Error.Message}"
                    : $"{name} must compile via vkd3d-shader");

            AssertParity(name, compiled.Value.Bytes);
        }
    }

    private static void AssertParity(string name, ReadOnlyMemory<byte> dxbc)
    {
        // Re-assert the gate the test attributes already enforce, so the platform
        // analyzer (CA1416) sees the guard on the D3DReflect oracle call path.
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The D3DReflect oracle requires Windows.");

        Result<ReflectedEffect, ShaderError> oracle = D3DReflectOracle.Extract(dxbc);
        oracle.IsSuccess.Should().BeTrue(
            because: oracle.IsFailure
                ? $"{name}: D3DReflect oracle failed: {oracle.Error.Message}"
                : $"{name} must reflect via D3DReflect");

        Result<ReflectedEffect, ShaderError> managed = RdefReader.Read(dxbc.Span, $"{name}.hlsl");
        managed.IsSuccess.Should().BeTrue(
            because: managed.IsFailure
                ? $"{name}: managed RdefReader failed: {managed.Error.Message}"
                : $"{name} must reflect via the managed reader");

        using (new AssertionScope(name))
        {
            // Deep equality, order-sensitive everywhere: cbuffer/variable/binding order
            // is meaningful (it drives parameter order in the .mgfx).
            managed.Value.Should().BeEquivalentTo(
                oracle.Value,
                options => options.WithStrictOrdering(),
                because: $"the managed reader must match the D3DReflect oracle for {name}");
        }
    }
}
