#nullable enable

using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL.Ast;

namespace ShadowDusk.HLSL.Reflection;

public static class ParameterListBuilder
{
    public static IReadOnlyList<ParameterReflection> Build(
        ReflectedEffect dxilReflection,
        IReadOnlyList<ParameterAnnotation>? fxAnnotations,
        bool includeSamplerParameters = true)
    {
        var annotationLookup = BuildAnnotationLookup(fxAnnotations);
        var parameters = new List<ParameterReflection>();

        foreach (ConstantBufferReflection cbuffer in dxilReflection.ConstantBuffers)
        {
            foreach (VariableReflection variable in cbuffer.Variables)
            {
                parameters.Add(new ParameterReflection
                {
                    Name        = variable.Name,
                    Class       = variable.ParameterClass,
                    Type        = variable.ParameterType,
                    Rows        = variable.Rows,
                    Columns     = variable.Columns,
                    Elements    = variable.Elements,
                    Annotations = annotationLookup.TryGetValue(variable.Name, out var annots)
                                  ? annots
                                  : null,
                });
            }
        }

        foreach (TextureReflection texture in dxilReflection.Textures.OrderBy(t => t.BindSlot))
        {
            parameters.Add(new ParameterReflection
            {
                Name        = texture.Name,
                Class       = EffectParameterClass.Object,
                Type        = MapTextureDimensionToType(texture.Dimension),
                Rows        = 0,
                Columns     = 0,
                Elements    = 0,
                Annotations = annotationLookup.TryGetValue(texture.Name, out var annots)
                              ? annots
                              : null,
            });
        }

        // mgfxc folds a sampler+texture into the single texture (Object) parameter
        // and does NOT emit a standalone parameter for the sampler. The DX path sets
        // includeSamplerParameters=false to match the golden exactly. (The GL path
        // keeps emitting them: that output is Phase-17-validated as rendering-correct,
        // so its behavior is preserved byte-for-byte.)
        if (!includeSamplerParameters)
            return parameters;

        foreach (SamplerReflection sampler in dxilReflection.Samplers.OrderBy(s => s.BindSlot))
        {
            parameters.Add(new ParameterReflection
            {
                Name        = sampler.Name,
                Class       = EffectParameterClass.Object,
                Type        = EffectParameterType.Texture,
                Rows        = 0,
                Columns     = 0,
                Elements    = 0,
                Annotations = annotationLookup.TryGetValue(sampler.Name, out var annots)
                              ? annots
                              : null,
            });
        }

        return parameters;
    }

    private static Dictionary<string, IReadOnlyList<AnnotationReflection>> BuildAnnotationLookup(
        IReadOnlyList<ParameterAnnotation>? fxAnnotations)
    {
        var lookup = new Dictionary<string, IReadOnlyList<AnnotationReflection>>(StringComparer.Ordinal);
        if (fxAnnotations is null)
            return lookup;

        foreach (ParameterAnnotation pa in fxAnnotations)
        {
            var entries = pa.Entries
                .Select(e => new AnnotationReflection { Name = e.Name, Value = e.Value })
                .ToList();

            lookup[pa.ParameterName] = entries;
        }

        return lookup;
    }

    private static EffectParameterType MapTextureDimensionToType(TextureDimension dimension) =>
        dimension switch
        {
            TextureDimension.Texture1D   => EffectParameterType.Texture1D,
            TextureDimension.Texture2D   => EffectParameterType.Texture2D,
            TextureDimension.Texture3D   => EffectParameterType.Texture3D,
            TextureDimension.TextureCube => EffectParameterType.TextureCube,
            _                            => EffectParameterType.Texture,
        };
}
