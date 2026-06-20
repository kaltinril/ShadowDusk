namespace ShadowDusk.ShaderToy;

/// <summary>The base scalar kind underlying a GLSL type.</summary>
internal enum ScalarKind
{
    Void,
    Bool,
    Int,
    Float,
    Sampler,
    Unknown,
}

/// <summary>
/// A minimal GLSL type descriptor — enough for the type-inference pass to know whether an
/// expression is scalar, a vector (and its width), or a matrix. This is deliberately not a full
/// type system: it exists to drive the two semantic traps (matrix multiply order, <c>mod</c> sign).
/// </summary>
internal readonly record struct GlslType(ScalarKind Scalar, int Rows, int Cols)
{
    /// <summary>A scalar of <paramref name="kind"/> (Rows = Cols = 1).</summary>
    public static GlslType ScalarOf(ScalarKind kind) => new(kind, 1, 1);

    /// <summary>A column vector of <paramref name="kind"/> with <paramref name="n"/> components.</summary>
    public static GlslType Vector(ScalarKind kind, int n) => new(kind, n, 1);

    /// <summary>A square matrix of floats, <paramref name="n"/>×<paramref name="n"/>.</summary>
    public static GlslType Matrix(int n) => new(ScalarKind.Float, n, n);

    public static readonly GlslType Unknown = new(ScalarKind.Unknown, 0, 0);

    /// <summary>True for an N×N matrix (N ≥ 2).</summary>
    public bool IsMatrix => Cols > 1 && Rows > 1;

    /// <summary>True for a vector (Rows ≥ 2, single column).</summary>
    public bool IsVector => Cols == 1 && Rows >= 2;

    /// <summary>True for a 1-component scalar.</summary>
    public bool IsScalar => Rows == 1 && Cols == 1;

    public bool IsKnown => Scalar != ScalarKind.Unknown;
}
