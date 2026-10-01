#nullable enable

using System.Text;
using System.Text.RegularExpressions;
using ShadowDusk.Core;

namespace ShadowDusk.Compiler.Raylib;

/// <summary>
/// Maps SPIRV-Cross's modern GLSL (the seam BEFORE the MonoGame rewriter) onto raylib's fixed
/// fragment-shader interface: the convention mapper Phase 59 §3.2 says is required.
///
/// <list type="bullet">
///   <item><c>TEXCOORD0</c> (vec2) → <c>fragTexCoord</c> and <c>COLOR0</c> (vec4) →
///   <c>fragColor</c>: exactly what raylib's built-in vertex shader writes, and exactly what
///   MonoGame's SpriteBatch supplies (texture coordinate, per-sprite tint). Every other interpolant
///   is refused (<c>SD0633</c>), because the built-in vertex shader writes nothing else.</item>
///   <item>The single fragment output → <c>finalColor</c>; anything else is refused (<c>SD0634</c>).</item>
///   <item>The sampler on texture unit 0 → <c>texture0</c>; the others keep the HLSL texture's
///   name. Only <c>sampler2D</c> converts (<c>SD0635</c>).</item>
///   <item>Uniform blocks (including DXC's <c>$Globals</c>) flatten to loose uniforms, because
///   raylib binds by <c>glGetUniformLocation</c> name and never through a uniform block. Matrices
///   are refused (<c>SD0636</c>): their row/column-major convention through raylib's
///   <c>SetShaderValueMatrix</c> is not proven.</item>
///   <item>Builtins whose meaning depends on the render target's Y orientation
///   (<c>gl_FragCoord</c>, <c>dFdy</c>, ...) are refused (<c>SD0634</c>): MonoGame and raylib flip
///   render targets in opposite directions, so the same expression reads different values.</item>
/// </list>
/// </summary>
internal static class RaylibGlslMapper
{
    /// <summary>One combined sampler as the converter resolved it.</summary>
    internal sealed record SamplerInput(
        string TextureName,
        string SamplerName,
        int Slot,
        IReadOnlyDictionary<string, string> BakedState);

    internal const string TexCoordVarying = "fragTexCoord";
    internal const string ColorVarying = "fragColor";
    internal const string OutputName = "finalColor";
    internal const string DrawTextureUniform = "texture0";

    // Names raylib's LoadShader resolves and its draw paths set on their own. A user uniform under
    // one of these would be silently overwritten by raylib each draw.
    private static readonly HashSet<string> RaylibReservedNames = new(StringComparer.Ordinal)
    {
        TexCoordVarying, ColorVarying, OutputName, DrawTextureUniform, "texture1", "texture2",
        "colDiffuse", "colSpecular", "colAmbient", "mvp", "matView", "matProjection", "matModel",
        "matNormal", "boneMatrices", "vertexPosition", "vertexTexCoord", "vertexTexCoord2",
        "vertexNormal", "vertexTangent", "vertexColor", "vertexBoneIds", "vertexBoneWeights",
    };

    // GLSL 330 keywords, reserved words, and the builtin functions a fragment shader calls. A
    // sampler renamed to one of these would fail to compile or shadow the builtin.
    private static readonly HashSet<string> GlslReservedNames = new(StringComparer.Ordinal)
    {
        "attribute", "const", "uniform", "varying", "layout", "centroid", "flat", "smooth",
        "noperspective", "break", "continue", "do", "for", "while", "switch", "case", "default",
        "if", "else", "in", "out", "inout", "float", "int", "void", "bool", "true", "false",
        "invariant", "discard", "return", "mat2", "mat3", "mat4", "vec2", "vec3", "vec4", "ivec2",
        "ivec3", "ivec4", "bvec2", "bvec3", "bvec4", "uint", "uvec2", "uvec3", "uvec4", "lowp",
        "mediump", "highp", "precision", "sampler2D", "sampler3D", "samplerCube", "struct",
        "common", "partition", "active", "asm", "class", "union", "enum", "typedef", "template",
        "this", "packed", "goto", "inline", "noinline", "volatile", "public", "static", "extern",
        "external", "interface", "long", "short", "double", "half", "fixed", "unsigned",
        "superp", "input", "output", "hvec2", "hvec3", "hvec4", "dvec2", "dvec3", "dvec4",
        "fvec2", "fvec3", "fvec4", "sampler3DRect", "filter", "image1D", "image2D", "image3D",
        "imageCube", "sizeof", "cast", "namespace", "using", "row_major", "patch", "sample",
        "subroutine", "texture", "textureSize", "texelFetch", "textureLod", "textureGrad",
        "textureOffset", "textureProj", "mix", "step", "smoothstep", "clamp", "min", "max", "abs",
        "sign", "floor", "ceil", "fract", "mod", "pow", "exp", "exp2", "log", "log2", "sqrt",
        "inversesqrt", "sin", "cos", "tan", "asin", "acos", "atan", "radians", "degrees",
        "length", "distance", "dot", "cross", "normalize", "reflect", "refract", "faceforward",
        "round", "roundEven", "trunc", "isnan", "isinf", "dFdx", "dFdy", "fwidth", "noise1",
        "noise2", "noise3", "noise4", "main", "not", "any", "all", "equal", "notEqual",
        "lessThan", "lessThanEqual", "greaterThan", "greaterThanEqual", "transpose", "inverse",
        "determinant", "outerProduct", "matrixCompMult", "modf", "sinh", "cosh", "tanh", "asinh",
        "acosh", "atanh", "floatBitsToInt", "floatBitsToUint", "intBitsToFloat", "uintBitsToFloat",
    };

    private static readonly Regex VersionLine = new(
        @"^\s*#version\s+\d+.*$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex InputDecl = new(
        @"^[ \t]*(?<qual>(?:\w+[ \t]+)*?)in[ \t]+(?<type>\w+)[ \t]+(?<name>\w+)[ \t]*;[ \t]*\r?\n?",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex OutputDecl = new(
        @"^[ \t]*(?:layout\s*\([^)]*\)\s*)?out[ \t]+(?<type>\w+)[ \t]+(?<name>\w+)[ \t]*;[ \t]*\r?\n?",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex SamplerDecl = new(
        @"^[ \t]*uniform[ \t]+(?<type>\w*sampler\w*)[ \t]+(?<name>\w+)[ \t]*;[ \t]*\r?\n?",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex UniformBlock = new(
        @"^[ \t]*(?:layout\s*\([^)]*\)\s*)?uniform[ \t]+(?<block>\w+)\s*\{(?<members>[^}]*)\}\s*(?<instance>\w*)\s*;[ \t]*\r?\n?",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex BlockMember = new(
        @"^(?:layout\s*\((?<layout>[^)]*)\)\s*)?(?<type>\w+)\s+(?<name>\w+)\s*(?:\[\s*(?<len>\d+)\s*\])?$",
        RegexOptions.Compiled);

    // Builtins whose value depends on which way the render target is flipped. MonoGame flips
    // render targets with its posFixup (row 0 = top); raylib's BeginTextureMode does not.
    private static readonly Regex OrientationDependent = new(
        @"\b(gl_\w+|dFdy|dFdyFine|dFdyCoarse|interpolateAt\w+)\b", RegexOptions.Compiled);

    private static readonly Regex ScalarOrVectorType = new(
        @"^(float|int|uint|bool|[iub]?vec[234])$", RegexOptions.Compiled);

    private static readonly Regex Semantic = new(@"^in_var_(?<base>[A-Za-z_]+?)(?<index>\d*)$", RegexOptions.Compiled);

    public static Result<RaylibShader, ShaderError[]> Map(
        string glsl,
        IReadOnlyList<SamplerInput> samplers,
        string hlslSource,
        string sourceName)
    {
        var warnings = new List<ShaderError>();

        Match orientation = OrientationDependent.Match(glsl);
        if (orientation.Success)
        {
            return Fail(sourceName, "SD0634",
                $"the pixel shader uses '{orientation.Value}', whose value depends on the render " +
                "target's Y orientation (or on pipeline state a raylib shader string cannot carry). " +
                "MonoGame flips render targets one way and raylib's BeginTextureMode the other, so " +
                "the same expression reads different values in the two runtimes. Refusing rather " +
                "than emitting a shader that renders mirrored. Derive the coordinate from " +
                "TEXCOORD0 (and pass the target size as a uniform) instead.");
        }

        // Reject before renaming: a source identifier already spelled like a raylib name would
        // merge with it after the rename.
        foreach (string reserved in RaylibReservedNames)
        {
            if (Regex.IsMatch(glsl, $@"\b{reserved}\b"))
            {
                return Fail(sourceName, "SD0635",
                    $"the shader uses the identifier '{reserved}', which raylib reserves (it binds " +
                    "or sets that name itself). After conversion the two would collide and raylib " +
                    "would silently overwrite or shadow it. Rename it in the .fx.");
            }
        }

        string text = VersionLine.Replace(glsl, "#version 330", 1);

        // 1. Interpolants.
        string? texCoordVar = null, colorVar = null;
        foreach (Match input in InputDecl.Matches(text))
        {
            string qualifiers = input.Groups["qual"].Value.Trim();
            string type = input.Groups["type"].Value;
            string name = input.Groups["name"].Value;
            Match sem = Semantic.Match(name);
            string semantic = sem.Success
                ? sem.Groups["base"].Value.ToUpperInvariant() + (sem.Groups["index"].Value.Length > 0 ? sem.Groups["index"].Value : "0")
                : name;

            if (qualifiers.Length > 0)
            {
                return Fail(sourceName, "SD0633",
                    $"the interpolant '{semantic}' is declared '{qualifiers}'. raylib's built-in " +
                    "vertex shader writes plain smooth-interpolated outputs, so this interpolation " +
                    "mode cannot be reproduced.");
            }

            if (semantic == "TEXCOORD0" && type == "vec2")
            {
                texCoordVar = name;
                continue;
            }
            if (semantic == "COLOR0" && type == "vec4")
            {
                colorVar = name;
                continue;
            }

            string hint = semantic is "TEXCOORD0" or "COLOR0"
                ? $" It is declared as {type}; raylib's vertex shader writes " +
                  (semantic == "TEXCOORD0" ? "fragTexCoord as vec2 (use float2)." : "fragColor as vec4 (use float4).")
                : " raylib's built-in vertex shader supplies only TEXCOORD0 (fragTexCoord, vec2) " +
                  "and COLOR0 (fragColor, vec4); a custom vertex stage is outside this converter.";
            return Fail(sourceName, "SD0633",
                $"the pixel shader reads the interpolant '{semantic}', which raylib cannot supply as " +
                $"declared.{hint} Refusing rather than emitting a shader that fails to link or " +
                "reads an undefined value.");
        }

        text = InputDecl.Replace(text, "");
        var header = new StringBuilder();
        if (texCoordVar is not null)
        {
            header.Append($"in vec2 {TexCoordVarying};\n");
            text = ReplaceWord(text, texCoordVar, TexCoordVarying);
        }
        if (colorVar is not null)
        {
            header.Append($"in vec4 {ColorVarying};\n");
            text = ReplaceWord(text, colorVar, ColorVarying);
        }

        // 2. The fragment output.
        var outputs = OutputDecl.Matches(text);
        if (outputs.Count != 1 || outputs[0].Groups["type"].Value != "vec4")
        {
            string found = string.Join(", ", outputs.Select(o => $"{o.Groups["type"].Value} {o.Groups["name"].Value}"));
            return Fail(sourceName, "SD0634",
                $"raylib fragment shaders write exactly one vec4 output (finalColor); this one " +
                $"declares [{found}]. Multiple render targets and depth output cannot be converted.");
        }
        text = OutputDecl.Replace(text, "");
        text = ReplaceWord(text, outputs[0].Groups["name"].Value, OutputName);
        header.Append($"out vec4 {OutputName};\n");

        // 3. Samplers, matched to the converter's pairs in SPIRV-Cross declaration order.
        var samplerDecls = SamplerDecl.Matches(text);
        foreach (Match decl in samplerDecls)
        {
            if (decl.Groups["type"].Value != "sampler2D")
            {
                return Fail(sourceName, "SD0635",
                    $"the shader samples a '{decl.Groups["type"].Value}'. raylib binds shader " +
                    "textures as 2D textures only (SetShaderValueTexture), so only Texture2D/sampler2D " +
                    "converts.");
            }
        }
        if (samplerDecls.Count != samplers.Count)
        {
            return Fail(sourceName, "SD0635",
                $"internal mismatch: SPIRV-Cross declared {samplerDecls.Count} samplers but the " +
                $"SPIR-V pair extraction found {samplers.Count}. Refusing rather than guessing " +
                "which texture binds where.");
        }

        var samplerResults = new List<RaylibSampler>();
        var renames = new List<(string From, string To)>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < samplers.Count; i++)
        {
            SamplerInput s = samplers[i];
            bool drawTexture = s.Slot == 0;
            string uniform = drawTexture ? DrawTextureUniform : s.TextureName;

            if (!drawTexture && (GlslReservedNames.Contains(uniform) || RaylibReservedNames.Contains(uniform)
                                 || uniform.StartsWith("gl_", StringComparison.Ordinal)
                                 || Regex.IsMatch(glsl, $@"\b{Regex.Escape(uniform)}\b")))
            {
                return Fail(sourceName, "SD0635",
                    $"the texture '{s.TextureName}' would become the raylib sampler uniform " +
                    $"'{uniform}', which collides with a GLSL keyword, builtin, raylib name, or " +
                    "another identifier in the shader. Rename the texture in the .fx.");
            }
            if (!usedNames.Add(uniform))
            {
                return Fail(sourceName, "SD0635",
                    $"the texture '{s.TextureName}' is sampled through more than one sampler. raylib " +
                    "keeps filtering and addressing on the texture itself, so one texture cannot be " +
                    "read with two sampler states, and the two sampler uniforms would share a name.");
            }

            renames.Add((samplerDecls[i].Groups["name"].Value, uniform));
            samplerResults.Add(new RaylibSampler(uniform, s.TextureName, s.SamplerName, drawTexture, s.BakedState));

            if (s.BakedState.Count > 0)
            {
                string state = string.Join(", ", s.BakedState.Select(kv => $"{kv.Key} = {kv.Value}"));
                warnings.Add(new ShaderError(
                    File: sourceName, Line: 0, Column: 0, Code: "SD0637",
                    Message: $"sampler '{s.SamplerName}' bakes sampler state ({state}). A raylib " +
                             "shader cannot carry it: apply it to the texture bound to " +
                             $"'{uniform}' with SetTextureFilter/SetTextureWrap, or the texture's " +
                             "own filter and wrap are used instead.",
                    Severity: ShaderErrorSeverity.Warning));
            }
        }
        text = SamplerDecl.Replace(text, m => $"uniform sampler2D {m.Groups["name"].Value};\n");
        foreach ((string from, string to) in renames)
            text = ReplaceWord(text, from, to);

        // 4. Uniform blocks -> loose uniforms.
        var uniforms = new List<RaylibUniform>();
        var instances = new List<string>();
        ShaderError? blockError = null;
        text = UniformBlock.Replace(text, m =>
        {
            if (m.Groups["instance"].Value.Length > 0)
                instances.Add(m.Groups["instance"].Value);

            var sb = new StringBuilder();
            foreach (string raw in m.Groups["members"].Value.Split(';'))
            {
                string member = Regex.Replace(raw.Trim(), @"\s+", " ");
                if (member.Length == 0)
                    continue;

                Match mm = BlockMember.Match(member);
                if (!mm.Success)
                {
                    blockError ??= Error(sourceName, "SD0636",
                        $"the uniform declaration '{member}' has a shape this converter does not model.");
                    continue;
                }

                string type = mm.Groups["type"].Value;
                string name = mm.Groups["name"].Value;
                if (type.StartsWith("mat", StringComparison.Ordinal) || mm.Groups["layout"].Success)
                {
                    blockError ??= Error(sourceName, "SD0636",
                        $"the uniform '{name}' is a matrix ({type}). Its row/column-major convention " +
                        "through raylib's SetShaderValueMatrix is not proven by the raylib render gate, " +
                        "so it is refused rather than risking a transposed result. Pass the rows as " +
                        "float4 uniforms instead.");
                    continue;
                }
                if (!ScalarOrVectorType.IsMatch(type))
                {
                    blockError ??= Error(sourceName, "SD0636",
                        $"the uniform '{name}' has the struct type '{type}'. raylib's SetShaderValue " +
                        "binds scalars and vectors by name; struct members (and any matrix inside them) " +
                        "are not modeled, so it is refused. Declare the members as separate uniforms.");
                    continue;
                }

                // SPIRV-Cross renames a member that collides with a GLSL reserved word ('input' ->
                // '_input'). The consumer looks up the HLSL name and would bind nothing, so a GLSL
                // name the source never spells is refused rather than shipped.
                if (!Regex.IsMatch(hlslSource, $@"\b{Regex.Escape(name)}\b"))
                {
                    string hlslName = name.Trim('_');
                    blockError ??= Error(sourceName, "SD0635",
                        $"the uniform '{hlslName}' comes out of SPIRV-Cross as '{name}' (a GLSL reserved " +
                        $"name is renamed), so GetShaderLocation(\"{hlslName}\") would bind nothing. " +
                        "Rename it in the .fx.");
                    continue;
                }

                int length = mm.Groups["len"].Success ? int.Parse(mm.Groups["len"].Value) : 0;
                uniforms.Add(new RaylibUniform(name, type, length));
                sb.Append($"uniform {member};\n");
            }
            return sb.ToString();
        });
        if (blockError is not null)
            return Result<RaylibShader, ShaderError[]>.Fail([blockError]);

        foreach (string instance in instances)
            text = Regex.Replace(text, $@"\b{Regex.Escape(instance)}\s*\.\s*", "");

        // #extension directives must precede every declaration, so the interface goes after
        // SPIRV-Cross's leading preprocessor block rather than straight after #version.
        string[] lines = VersionLine.Replace(text, "").Trim().Split('\n');
        int preamble = 0;
        while (preamble < lines.Length &&
               (lines[preamble].TrimStart().StartsWith('#') || lines[preamble].Trim().Length == 0))
            preamble++;
        string directives = string.Join('\n', lines[..preamble]).Trim();
        string declarations = string.Join('\n', lines[preamble..]);

        string fragment = Regex.Replace(
            "#version 330\n" +
            "// Generated by ShadowDusk's raylib converter (HLSL -> DXC -> SPIRV-Cross -> raylib glsl330).\n" +
            "// Load with Raylib.LoadShaderFromMemory(null, <this>) so raylib's built-in vertex shader\n" +
            "// supplies fragTexCoord and fragColor.\n" +
            (directives.Length > 0 ? directives + "\n" : "") +
            "\n" + header + "\n" +
            declarations.Trim() + "\n",
            @"(\r?\n){3,}", "\n\n");

        return Result<RaylibShader, ShaderError[]>.Ok(
            new RaylibShader(fragment, uniforms, samplerResults, warnings));

    }

    private static string ReplaceWord(string input, string word, string replacement) =>
        Regex.Replace(input, $@"\b{Regex.Escape(word)}\b", replacement);

    private static ShaderError Error(string file, string code, string message) =>
        new(File: file, Line: 0, Column: 0, Code: code, Message: message);

    private static Result<RaylibShader, ShaderError[]> Fail(string file, string code, string message) =>
        Result<RaylibShader, ShaderError[]>.Fail([Error(file, code, message)]);
}
