using System.Text;
using ShadowDusk.ShaderToy.Ast;

namespace ShadowDusk.ShaderToy;

/// <summary>
/// The single public entry point of the ShaderToy → HLSL <c>.fx</c> conversion tool (Phase 46).
/// Out-of-band: it depends on nothing in the ShadowDusk compiler pipeline and only emits <c>.fx</c>
/// text. Unsupported constructs produce a fatal, located diagnostic (fail loudly) rather than
/// silently-wrong output.
/// </summary>
public static class ShaderToyConverter
{
    /// <summary>
    /// Convert a single-pass ShaderToy GLSL "image" shader (optionally with a "Common" tab via
    /// <see cref="ConvertOptions.CommonSource"/>) into a self-contained HLSL <c>.fx</c>.
    /// </summary>
    /// <param name="shaderToyGlsl">The ShaderToy "Image" tab source.</param>
    /// <param name="options">Conversion options; defaults are used when null.</param>
    /// <returns>
    /// A <see cref="ConvertResult"/>. On success <see cref="ConvertResult.Success"/> is true,
    /// <see cref="ConvertResult.Fx"/> holds the <c>.fx</c> text, and
    /// <see cref="ConvertResult.UsedUniforms"/> lists the referenced uniforms. On any error,
    /// <see cref="ConvertResult.Success"/> is false, <see cref="ConvertResult.Fx"/> is null, and the
    /// diagnostics describe what was rejected and where.
    /// </returns>
    public static ConvertResult Convert(string shaderToyGlsl, ConvertOptions? options = null)
    {
        options ??= new ConvertOptions();
        var diagnostics = new List<ConvertDiagnostic>();

        if (shaderToyGlsl is null)
        {
            diagnostics.Add(new ConvertDiagnostic(
                DiagnosticSeverity.Error, "Source GLSL was null.", 0, 0));
            return Fail(diagnostics);
        }

        try
        {
            return ConvertCore(shaderToyGlsl, options, diagnostics);
        }
        catch (ConvertException ex)
        {
            diagnostics.Add(new ConvertDiagnostic(
                DiagnosticSeverity.Error, ex.Message, ex.Line, ex.Column, ex.Construct));
            return Fail(diagnostics);
        }
    }

    private static ConvertResult ConvertCore(
        string imageSource, ConvertOptions options, List<ConvertDiagnostic> diagnostics)
    {
        // Reject obvious multipass / non-image entry points before doing real work (cheap guardrail
        // against silently producing a wrong single-pass effect from a multipass shader).
        RejectUnsupportedEntryPoints(imageSource, diagnostics);
        if (options.CommonSource is { } common)
        {
            RejectUnsupportedEntryPoints(common, diagnostics);
        }

        if (diagnostics.Count > 0 && options.StopOnFirstError)
        {
            return Fail(diagnostics);
        }

        // Preprocess + parse the Common tab (if any) and the Image tab.
        var pre = new Preprocessor();
        string commonPp = options.CommonSource is null ? string.Empty : pre.Process(options.CommonSource);
        string imagePp = pre.Process(imageSource);

        TranslationUnit commonUnit = ParseUnit(commonPp);
        TranslationUnit imageUnit = ParseUnit(imagePp);

        // Merge: Common globals/functions come first.
        var globals = new List<GlobalConstDecl>(commonUnit.Globals);
        globals.AddRange(imageUnit.Globals);
        var functions = new List<FunctionDecl>(commonUnit.Functions);
        functions.AddRange(imageUnit.Functions);
        var customUniforms = new List<CustomUniformDecl>(commonUnit.CustomUniforms);
        customUniforms.AddRange(imageUnit.CustomUniforms);
        var mutableGlobals = new List<GlobalVarDecl>(commonUnit.MutableGlobals);
        mutableGlobals.AddRange(imageUnit.MutableGlobals);
        var structs = new List<StructDecl>(commonUnit.Structs);
        structs.AddRange(imageUnit.Structs);
        var fragmentOutputs = new List<FragmentOutputDecl>(commonUnit.FragmentOutputs);
        fragmentOutputs.AddRange(imageUnit.FragmentOutputs);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> a in commonUnit.Aliases)
        {
            aliases[a.Key] = a.Value;
        }

        foreach (KeyValuePair<string, string> a in imageUnit.Aliases)
        {
            aliases[a.Key] = a.Value;
        }

        var merged = new TranslationUnit
        {
            Globals = globals,
            Functions = functions,
            CustomUniforms = customUniforms,
            MutableGlobals = mutableGlobals,
            Structs = structs,
            FragmentOutputs = fragmentOutputs,
            Aliases = aliases,
        };

        // Detect the entry convention: a ShaderToy `void mainImage(out vec4, in vec2)` OR a plain-GLSL
        // `void main()`. When BOTH are present we prefer ShaderToy (`mainImage` is canonical for a
        // ShaderToy-derived file; the standalone `void main()` is just a desktop-runner wrapper that our
        // harness replaces) and DROP the user `main` with a Warning. NEITHER is the existing "no entry
        // point" reject.
        EntryMode entryMode = DetectEntryMode(functions, diagnostics);
        FragmentOutputDecl? userFragmentOutput = null;
        if (entryMode == EntryMode.ShaderToy)
        {
            ValidateMainImage(functions);

            // ShaderToy mode wins even when a standalone `void main()` wrapper is also present. The
            // wrapper (e.g. `void main(){ mainImage(gl_FragColor, gl_FragCoord.xy); }`) is NOT translated
            // or emitted — our harness synthesizes its own fullscreen VS/PS that calls `mainImage`
            // directly. Drop it here so its body (which references the GL-only `gl_FragColor`/
            // `gl_FragCoord` write target the ShaderToy harness does not declare) is never emitted and
            // leaves no dangling reference. Everything else (helpers, `mainImage`, globals) is unaffected.
            if (functions.Any(f => f.Name == "main"))
            {
                functions.RemoveAll(f => f.Name == "main");
            }
        }
        else
        {
            userFragmentOutput = ResolveMainEntry(functions, merged);
        }

        // Type-inference + emit.
        var types = new TypeInference(merged);

        // In plain-GLSL `main()` mode (G2) the fragment output (`gl_FragColor` or the user-declared
        // `out vec4 <name>;`) and `gl_FragCoord` are predefined vec4 file-scope identifiers the shader
        // body reads/writes; register them so the body's references resolve (the harness declares each
        // as a `static float4` global and the synthesized PS bridges them — see HarnessGenerator).
        string fragmentOutputName = "gl_FragColor";
        if (entryMode == EntryMode.PlainGlsl)
        {
            fragmentOutputName = userFragmentOutput?.Name ?? "gl_FragColor";
            types.DeclareBuiltinGlobal(fragmentOutputName, GlslType.Vector(ScalarKind.Float, 4));
            types.DeclareBuiltinGlobal("gl_FragCoord", GlslType.Vector(ScalarKind.Float, 4));
        }

        string entryFunctionName = entryMode == EntryMode.ShaderToy ? "mainImage" : "main";
        var emitter = new HlslEmitter(types);
        emitter.SetUserFunctions(functions.Where(f => f.Name != entryFunctionName).Select(f => f.Name)
            .Concat(new[] { entryFunctionName }));
        emitter.SetCustomUniforms(merged.CustomUniforms.Select(c => c.Name));
        emitter.SetStructs(merged.Structs);

        // G6: struct declarations + their factory functions are emitted first (before const globals and
        // functions) so every later use of the struct type / its constructor resolves.
        var structsSb = new StringBuilder();
        foreach (StructDecl s in merged.Structs)
        {
            structsSb.Append(emitter.EmitStruct(s));
            structsSb.AppendLine();
        }

        var globalsSb = new StringBuilder();
        foreach (GlobalConstDecl g in merged.Globals)
        {
            globalsSb.Append(emitter.EmitGlobalConst(g));
        }

        // G1: top-level mutable globals emit as HLSL `static` globals (after the const globals).
        foreach (GlobalVarDecl gv in merged.MutableGlobals)
        {
            globalsSb.Append(emitter.EmitGlobalVar(gv));
        }

        // G4: translate each custom-uniform default initializer (if any) to an HLSL expression the
        // harness emits as the parameter's default. Done here (not in the harness) so the shared
        // emitter does the expression translation (intrinsic renames, matrix order, etc.).
        var customUniformDefaults = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CustomUniformDecl cu in merged.CustomUniforms)
        {
            if (cu.Initializer is not null)
            {
                customUniformDefaults[cu.Name] = emitter.EmitUniformDefault(cu.TypeName, cu.Initializer);
            }
        }

        var fnSb = new StringBuilder();
        foreach (FunctionDecl f in merged.Functions)
        {
            // Skip pure prototypes (empty body, no statements) — we only emit definitions.
            if (f.Body.Statements.Count == 0 && f.Parameters.Count >= 0 && IsPrototype(f, merged.Functions))
            {
                continue;
            }

            fnSb.Append(emitter.EmitFunction(f));
            fnSb.AppendLine();
        }

        var harness = new HarnessGenerator();
        string fx = harness.Generate(
            options,
            emitter.ReferencedUniforms,
            merged.CustomUniforms,
            customUniformDefaults,
            emitter.UsedGlslMod,
            structsSb.ToString(),
            globalsSb.ToString(),
            fnSb.ToString(),
            entryMode,
            fragmentOutputName);

        // If any Error accumulated (e.g. a banned entry point detected up front without
        // StopOnFirstError), the conversion is NOT a success even though emission ran to completion.
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return Fail(diagnostics);
        }

        // Drivable parameters = the referenced ShaderToy built-ins PLUS every accepted custom uniform
        // (a custom uniform is always host-driven, whether or not the body references it).
        var used = new List<string>(emitter.ReferencedUniforms);
        foreach (CustomUniformDecl cu in merged.CustomUniforms)
        {
            if (!used.Contains(cu.Name))
            {
                used.Add(cu.Name);
            }
        }

        return new ConvertResult
        {
            Success = true,
            Fx = fx,
            Diagnostics = diagnostics,
            UsedUniforms = used,
        };
    }

    /// <summary>True if this declaration is a forward prototype superseded by a later definition.</summary>
    private static bool IsPrototype(FunctionDecl candidate, IReadOnlyList<FunctionDecl> all)
    {
        if (candidate.Body.Statements.Count != 0)
        {
            return false;
        }

        // A defined twin (same name, non-empty body) exists → this is just a prototype.
        return all.Any(f => f.Name == candidate.Name && f.Body.Statements.Count > 0);
    }

    private static TranslationUnit ParseUnit(string preprocessed)
    {
        List<Token> tokens = new Lexer(preprocessed).Tokenize();
        return new Parser(tokens).Parse();
    }

    /// <summary>
    /// Decide which entry convention the shader uses (G2): a ShaderToy
    /// <c>void mainImage(out vec4, in vec2)</c> or a plain-GLSL <c>void main()</c>. When BOTH are
    /// defined we PREFER ShaderToy mode (<c>mainImage</c> is canonical for a ShaderToy-derived file) and
    /// emit a <see cref="DiagnosticSeverity.Warning"/> noting the standalone <c>void main()</c> wrapper
    /// is ignored in favor of <c>mainImage</c> (the caller drops it; our harness replaces it). A shader
    /// with NEITHER falls through to the existing "no entry point" reject in <see cref="ValidateMainImage"/>
    /// (ShaderToy is the default mode). When ONLY <c>main</c> is defined we use plain-GLSL mode (G2).
    /// (Signatures are validated by the per-mode validator; here we only pick the mode by which
    /// entry-NAME is defined.)
    /// </summary>
    private static EntryMode DetectEntryMode(
        IReadOnlyList<FunctionDecl> functions, List<ConvertDiagnostic> diagnostics)
    {
        bool hasMainImage = functions.Any(f => f.Name == "mainImage");
        bool hasMain = functions.Any(f => f.Name == "main");

        if (hasMainImage && hasMain)
        {
            // Both entries present: prefer the canonical ShaderToy `mainImage` and warn that the
            // standalone `void main()` wrapper (a glslViewer / Bonzomatic / desktop-runner shim that our
            // harness replaces) is ignored. This is a Warning, not a reject: ~a third of real third-party
            // ShaderToy shaders ship with such a wrapper, and `mainImage` is unambiguously the shader.
            FunctionDecl at = functions.First(f => f.Name == "main");
            diagnostics.Add(new ConvertDiagnostic(
                DiagnosticSeverity.Warning,
                "The shader defines BOTH a ShaderToy 'mainImage' and a standalone 'void main()'. " +
                "Using 'mainImage' as the entry and ignoring the 'void main()' wrapper (our harness " +
                "generates its own fullscreen pass that calls 'mainImage' directly).",
                at.Line, at.Column, "main"));
            return EntryMode.ShaderToy;
        }

        return hasMain && !hasMainImage ? EntryMode.PlainGlsl : EntryMode.ShaderToy;
    }

    /// <summary>
    /// Validate the plain-GLSL <c>void main()</c> entry (G2) and resolve its fragment output. The entry
    /// must be <c>void main()</c> with no parameters. The fragment output is EITHER a user-declared
    /// top-level <c>out vec4 &lt;name&gt;;</c> (returned) OR the legacy <c>gl_FragColor</c> write target;
    /// at most one user <c>out vec4</c> is allowed. A <c>main</c> with no discoverable fragment output
    /// (no user <c>out vec4</c> AND no <c>gl_FragColor</c> write anywhere in the source) is a loud reject.
    /// </summary>
    private static FragmentOutputDecl? ResolveMainEntry(
        IReadOnlyList<FunctionDecl> functions, TranslationUnit unit)
    {
        List<FunctionDecl> entries = functions.Where(f => f.Name == "main").ToList();
        if (entries.Count == 0)
        {
            throw new ConvertException(
                "No entry point was found. Provide a ShaderToy " +
                "'void mainImage(out vec4 fragColor, in vec2 fragCoord)' or a plain-GLSL 'void main()' " +
                "single-pass fragment shader.",
                0, 0, "main");
        }

        if (entries.Count > 1)
        {
            FunctionDecl dup = entries[1];
            throw new ConvertException("Multiple 'main' definitions found.", dup.Line, dup.Column, "main");
        }

        FunctionDecl main = entries[0];
        if (main.ReturnType != "void")
        {
            throw new ConvertException("'main' must return void.", main.Line, main.Column, "main");
        }

        if (main.Parameters.Count != 0)
        {
            throw new ConvertException(
                "A plain-GLSL 'main' entry must take no parameters ('void main()'). For a ShaderToy " +
                "shader use 'void mainImage(out vec4 fragColor, in vec2 fragCoord)' instead.",
                main.Line, main.Column, "main");
        }

        if (unit.FragmentOutputs.Count > 1)
        {
            FragmentOutputDecl dup = unit.FragmentOutputs[1];
            throw new ConvertException(
                "Multiple 'out vec4' fragment outputs declared; a single-pass fragment shader has one " +
                "color output.", dup.Line, dup.Column, dup.Name);
        }

        if (unit.FragmentOutputs.Count == 1)
        {
            return unit.FragmentOutputs[0];
        }

        // No user-declared out var: the shader must write the legacy gl_FragColor. Require that the
        // token appears in a main()-mode source, else there is no discoverable fragment output.
        if (!MentionsGlFragColor(main))
        {
            throw new ConvertException(
                "'main()' has no discoverable fragment output: declare a top-level 'out vec4 <name>;' " +
                "(GLSL ES 3.00 / 330) or write the legacy 'gl_FragColor' in main().",
                main.Line, main.Column, "main");
        }

        return null; // legacy gl_FragColor output
    }

    /// <summary>True if any statement/expression in <paramref name="main"/> references
    /// <c>gl_FragColor</c> (a cheap structural check so a <c>main()</c> with no output target is a clean,
    /// located reject rather than a downstream undeclared-identifier compile error).</summary>
    private static bool MentionsGlFragColor(FunctionDecl main) =>
        AstScan.MentionsIdentifier(main.Body, "gl_FragColor");

    private static void ValidateMainImage(IReadOnlyList<FunctionDecl> functions)
    {
        List<FunctionDecl> entries = functions.Where(f => f.Name == "mainImage").ToList();
        if (entries.Count == 0)
        {
            throw new ConvertException(
                "No 'void mainImage(out vec4 fragColor, in vec2 fragCoord)' entry point was found. " +
                "Only single-pass ShaderToy image shaders are supported.",
                0, 0, "mainImage");
        }

        if (entries.Count > 1)
        {
            FunctionDecl dup = entries[1];
            throw new ConvertException(
                "Multiple 'mainImage' definitions found.", dup.Line, dup.Column, "mainImage");
        }

        FunctionDecl main = entries[0];
        if (main.ReturnType != "void")
        {
            throw new ConvertException(
                "'mainImage' must return void.", main.Line, main.Column, "mainImage");
        }

        if (main.Parameters.Count != 2)
        {
            throw new ConvertException(
                "'mainImage' must take exactly (out vec4 fragColor, in vec2 fragCoord).",
                main.Line, main.Column, "mainImage");
        }

        ParamDecl p0 = main.Parameters[0];
        ParamDecl p1 = main.Parameters[1];
        if (p0.TypeName != "vec4" || p0.Qualifier != ParamQualifier.Out)
        {
            throw new ConvertException(
                "The first parameter of 'mainImage' must be 'out vec4'.", p0.Line, p0.Column, "mainImage");
        }

        if (p1.TypeName != "vec2")
        {
            throw new ConvertException(
                "The second parameter of 'mainImage' must be 'vec2 fragCoord'.", p1.Line, p1.Column, "mainImage");
        }
    }

    /// <summary>
    /// Reject the non-image ShaderToy entry points and multipass buffers up front. These are
    /// matched as whole-word occurrences so they are not confused with substrings.
    /// </summary>
    private static void RejectUnsupportedEntryPoints(string source, List<ConvertDiagnostic> diagnostics)
    {
        (string Token, string Message)[] banned =
        {
            ("mainSound", "Audio shaders ('mainSound') are outside the supported subset (single-pass image only)."),
            ("mainVR", "VR shaders ('mainVR') are outside the supported subset (single-pass image only)."),
            ("mainCubemap", "Cubemap shaders ('mainCubemap') are outside the supported subset (single-pass image only)."),
        };

        foreach ((string token, string message) in banned)
        {
            if (FindWholeWord(source, token, out int line, out int col))
            {
                diagnostics.Add(new ConvertDiagnostic(DiagnosticSeverity.Error, message, line, col, token));
            }
        }
    }

    private static bool FindWholeWord(string text, string word, out int line, out int col)
    {
        line = 0;
        col = 0;
        int idx = 0;
        while ((idx = text.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = idx == 0 || !(char.IsLetterOrDigit(text[idx - 1]) || text[idx - 1] == '_');
            int after = idx + word.Length;
            bool rightOk = after >= text.Length || !(char.IsLetterOrDigit(text[after]) || text[after] == '_');
            if (leftOk && rightOk)
            {
                ComputeLineCol(text, idx, out line, out col);
                return true;
            }

            idx = after;
        }

        return false;
    }

    private static void ComputeLineCol(string text, int index, out int line, out int col)
    {
        line = 1;
        col = 1;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                col = 1;
            }
            else if (text[i] != '\r')
            {
                col++;
            }
        }
    }

    private static ConvertResult Fail(IReadOnlyList<ConvertDiagnostic> diagnostics) => new()
    {
        Success = false,
        Fx = null,
        Diagnostics = diagnostics,
        UsedUniforms = Array.Empty<string>(),
    };
}
