#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// The FNA target's <c>SD0306</c> warning (issue #505): a column-major matrix whose initializer
/// default is not symmetric. FNA reads such a default as its transpose, for fxc's build and for
/// ours, while <c>EffectParameter.SetValue(Matrix)</c> of the same value renders as declared
/// (measured on FNA 26.06, both FNA3D drivers). ShadowDusk keeps the default byte-equal to
/// fxc's (see <c>project_decisions.md</c>), so this only tells the developer; it never changes
/// the compiled effect.
/// </summary>
internal static class FnaMatrixDefaultWarning
{
    private const int MatrixColumnsClass = 3;

    /// <summary>
    /// One warning naming every affected matrix across <paramref name="ctabs"/>, or
    /// <see langword="null"/> when none is. A <c>row_major</c> matrix (FNA reads its default as
    /// declared) and a symmetric default such as the identity (its transpose is itself) are not
    /// affected. Each stage's CTAB trims the default to the registers it reads, so the longest
    /// image of a global is the one checked, the same one the effect parameter is built from.
    /// </summary>
    public static ShaderError? Check(IReadOnlyList<CtabTable> ctabs, string sourceFile)
    {
        var longestByName = new Dictionary<string, CtabConstant>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (CtabTable ctab in ctabs)
        {
            foreach (CtabConstant constant in ctab.Constants)
            {
                // Non-square matrices with a default are refused later (SD0302).
                if (constant.Class != MatrixColumnsClass || constant.DefaultValue is null ||
                    constant.Rows != constant.Columns || constant.Rows < 2)
                {
                    continue;
                }

                if (!longestByName.TryGetValue(constant.Name, out CtabConstant? existing))
                {
                    longestByName[constant.Name] = constant;
                    order.Add(constant.Name);
                }
                else if (constant.DefaultValue.Count > existing.DefaultValue!.Count)
                {
                    longestByName[constant.Name] = constant;
                }
            }
        }

        List<string> affected = order
            .Where(name => !IsSymmetric(longestByName[name]))
            .ToList();
        if (affected.Count == 0)
            return null;

        string names = string.Join(", ", affected.Select(name => $"'{name}'"));
        return new ShaderError(
            File: sourceFile,
            Line: 0,
            Column: 0,
            Code: "SD0306",
            Message: $"FNA reads the initializer default of column-major matrix {names} as its transpose, " +
                     "with fxc's build too (this effect's default matches fxc's). To use the declared value, " +
                     "call EffectParameter.SetValue(Matrix) after loading the effect. Declaring the matrix " +
                     "row_major also makes the default read as declared, but SetValue(Matrix) then lands transposed.",
            Severity: ShaderErrorSeverity.Warning);
    }

    private static bool IsSymmetric(CtabConstant matrix)
    {
        IReadOnlyList<float>? values = matrix.LogicalDefaultValue();
        if (values is null)
            return true;

        int size = matrix.Rows;
        int elements = Math.Max(1, matrix.Elements);
        for (int element = 0; element < elements; element++)
        {
            int start = element * size * size;
            for (int row = 0; row < size; row++)
            {
                for (int column = row + 1; column < size; column++)
                {
                    // Equals, so a NaN default compares equal to itself.
                    if (!values[start + row * size + column].Equals(values[start + column * size + row]))
                        return false;
                }
            }
        }

        return true;
    }
}
