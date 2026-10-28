#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// A flattened effect parameter as exposed to the MonoGame runtime (the user-settable
/// surface, e.g. via <c>Effect.Parameters["Foo"]</c>), with its name, optional semantic,
/// type shape, and annotations.
/// </summary>
public sealed record ParameterReflection
{
    /// <summary>The parameter's name.</summary>
    public required string               Name           { get; init; }
    /// <summary>The HLSL semantic attached to the parameter, if any.</summary>
    public string?                       Semantic       { get; init; }
    /// <summary>The parameter's shape class (scalar, vector, matrix, object, …).</summary>
    public required EffectParameterClass Class          { get; init; }
    /// <summary>The parameter's element type.</summary>
    public required EffectParameterType  Type           { get; init; }
    /// <summary>The number of rows; 1 for scalars.</summary>
    public required int                  Rows           { get; init; }
    /// <summary>The number of columns; 1 for scalars.</summary>
    public required int                  Columns        { get; init; }
    /// <summary>The array element count; 1 when not an array.</summary>
    public required int                  Elements       { get; init; }
    /// <summary>The annotations declared on the parameter, if any.</summary>
    public IReadOnlyList<AnnotationReflection>? Annotations { get; init; }

    /// <summary>
    /// The default value the HLSL declaration gives the parameter (<c>float Scale = 2.0;</c>),
    /// or <see langword="null"/> when it is unknown.
    /// </summary>
    /// <remarks>
    /// <para><b>Layout.</b> <c>Rows * Columns * Math.Max(1, Elements)</c> floats, array element
    /// after array element, each element in logical row-major order (the cell at row <c>r</c>,
    /// column <c>c</c> is at index <c>r * Columns + c</c>; a <c>float2x3</c> is
    /// <c>m00 m01 m02 m10 m11 m12</c>, the order its constructor takes). No register padding,
    /// whatever the matrix packing, and not the column-by-column order of the blob in a
    /// <c>.mgfx</c>. <c>int</c> values are converted to floats, <c>uint</c> values by their unsigned
    /// value (so <c>uint U = 3000000000;</c> is 3000000000, not negative), and an integer beyond 2^24
    /// is not exactly representable. <c>bool</c> is 0 or 1 on every target. How a 32-bit word reads
    /// follows the HLSL declaration, not <see cref="Type"/>: reflection reports both <c>int</c> and
    /// <c>uint</c> as <c>Int32</c>, and on Vulkan a <c>bool</c> too.</para>
    /// <para><b>MGFX and KNIFX.</b> It is exactly the default blob the file carries for the
    /// parameter (the initializer, read off the preprocessed source and evaluated, so constructors,
    /// brace lists, folding of constants and the like all count). It is <see langword="null"/>
    /// where the file's blob is zeros the author did not write: the parameter has no initializer,
    /// or the initializer could not be evaluated (the compile then carries an <c>SD0017</c>
    /// warning). Struct, texture and sampler parameters are always <see langword="null"/>.</para>
    /// <para><b>FNA.</b> The same source reading as MGFX. A scalar or vector it has no value for
    /// takes the compiled constant table's; a matrix or array it cannot evaluate is
    /// <see langword="null"/>, because the compiler trims that table to the registers the shader
    /// reads.</para>
    /// <para><b>Vulkan.</b> SPIR-V has no bool in a uniform block, so a <c>bool</c> there has
    /// <see cref="Type"/> <c>Int32</c>, exactly as in real <c>mgfxc</c>'s Vulkan build (and in the
    /// <c>.mgfx</c>). Its default still reads as 0 or 1, because the value is read by the
    /// declaration's kind; a tool that needs to tell <c>bool</c> from <c>int</c> on Vulkan has only
    /// the source to go by. The file still carries the word <c>0xFFFFFFFF</c> for <c>true</c>, so
    /// the real engine's <c>EffectParameter.GetValueInt32()</c> on that parameter returns -1
    /// where this value is 1.</para>
    /// <para><b>Why from the source.</b> The compilers do not keep the initializer in a form
    /// reflection reads: DXC's DXIL reflection reports no default (and warns that the initializer
    /// is ignored) and DXC drops it from the SPIR-V.</para>
    /// </remarks>
    public IReadOnlyList<float>? DefaultValue { get; init; }
}
