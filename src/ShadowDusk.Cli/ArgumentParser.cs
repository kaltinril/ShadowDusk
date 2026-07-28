#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;

namespace ShadowDusk.Cli;

internal static class ArgumentParser
{
    public static string UsageText { get; } =
        """
        Usage: ShadowDuskCLI <SourceFile> <OutputFile> [options]

        Options:
          /Profile:<Platform>       Target platform. Default: DirectX_11
                                    Platforms: DirectX_11, DirectX_12, OpenGL, Vulkan, FNA
          /Debug                    Include debug information in output
          /Defines:<name=value;...> Preprocessor macros (mgfxc parity; ';' or ','
                                    separated; a bare name defines 1; repeatable)
          /I <path>                 Additional include search path (repeatable)
          /DxbcBackend:<Backend>    DXBC backend for DirectX_11. Default: vkd3d
                                    (cross-platform). d3dcompiler is the Windows-only
                                    correctness oracle; never required for correct output.
          --mgfx-version <10|11>    Output format version. Default: 10
          --target-runtime <name>   Output target runtime (picks backend + format together).
                                    Names: monogame-gl, monogame-dx, monogame-gl-v11,
                                    kni-knifx, fna. Overrides /Profile and --mgfx-version.
          --input-format <fmt>      Input language: auto (default), fx, glsl. ShaderToy / GLSL
                                    image shaders (.glsl/.frag/.fs) are auto-detected and converted
                                    to .fx before compiling; never required for correct output.
          --print-uniforms          Print the converted shader's drivable effect parameters to
                                    stderr (off by default; only affects ShaderToy/GLSL input).

        Unsupported platforms (exit 1): PlayStation4, XboxOne, Switch
        """;

    // Unknown flags are silently ignored so that future mgfxc flags MGCB may
    // pass (e.g. new MonoGame versions) do not break existing pipelines.
    public static Result<CliArguments, ShaderError> Parse(string[] args)
    {
        string? sourceFile = null;
        string? outputFile = null;
        PlatformTarget platform = PlatformTarget.DirectX;
        bool debug = false;
        var includePaths = new List<string>();
        int mgfxVersion = 10;
        DxbcBackend dxbcBackend = DxbcBackend.Vkd3d;
        CapabilityProfile? profile = null;
        InputFormat inputFormat = InputFormat.Auto;
        bool printUniforms = false;
        var defines = new List<UserDefine>();

        int i = 0;
        while (i < args.Length)
        {
            string token = args[i];

            if (IsFlag(token))
            {
                string flagBody = StripPrefix(token);

                if (flagBody.Equals("Debug", StringComparison.OrdinalIgnoreCase))
                {
                    debug = true;
                    i++;
                    continue;
                }

                if (flagBody.StartsWith("Profile:", StringComparison.OrdinalIgnoreCase))
                {
                    string profileValue = flagBody.Substring("Profile:".Length);
                    var profileResult = ParseProfile(profileValue);
                    if (profileResult.IsFailure)
                        return Result<CliArguments, ShaderError>.Fail(profileResult.Error);
                    platform = profileResult.Value;
                    i++;
                    continue;
                }

                if (flagBody.StartsWith("DxbcBackend:", StringComparison.OrdinalIgnoreCase))
                {
                    // Non-required escape hatch (the default, vkd3d, is the correct path
                    // on every OS); d3dcompiler opts in to the Windows-only fxc oracle.
                    string backendValue = flagBody.Substring("DxbcBackend:".Length);
                    if (backendValue.Equals("vkd3d", StringComparison.OrdinalIgnoreCase))
                        dxbcBackend = DxbcBackend.Vkd3d;
                    else if (backendValue.Equals("d3dcompiler", StringComparison.OrdinalIgnoreCase))
                        dxbcBackend = DxbcBackend.D3DCompiler;
                    else
                        return Result<CliArguments, ShaderError>.Fail(new ShaderError(
                            File: "",
                            Line: 0,
                            Column: 0,
                            Code: "X0006",
                            Message: $"Invalid /DxbcBackend value '{backendValue}'. Valid backends: vkd3d, d3dcompiler"));
                    i++;
                    continue;
                }

                if (flagBody.StartsWith("Defines:", StringComparison.OrdinalIgnoreCase))
                {
                    // mgfxc parity: MGCB's EffectProcessor forwards its Defines property as
                    // /Defines:NAME=VALUE;NAME2 (';'-joined; ',' tolerated). This real mgfxc
                    // flag used to fall into the unknown-flag ignore below, so the macros were
                    // silently dropped and #ifdef branches compiled out with exit 0 (bug-hunt
                    // 2026-07-27 M9). Repeatable; entries accumulate.
                    string definesValue = flagBody.Substring("Defines:".Length);
                    foreach (string entry in definesValue.Split(';', ','))
                    {
                        string trimmed = entry.Trim();
                        if (trimmed.Length == 0)
                            continue;
                        int eq = trimmed.IndexOf('=');
                        defines.Add(eq < 0
                            ? new UserDefine(trimmed)
                            : new UserDefine(trimmed[..eq].Trim(), trimmed[(eq + 1)..].Trim()));
                    }
                    i++;
                    continue;
                }

                if (flagBody.StartsWith("I:", StringComparison.OrdinalIgnoreCase))
                {
                    string path = flagBody.Substring(2);
                    if (path.Length > 0)
                        includePaths.Add(path);
                    i++;
                    continue;
                }

                if (flagBody.Equals("I", StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                    if (i < args.Length && !IsFlag(args[i]))
                    {
                        includePaths.Add(args[i]);
                        i++;
                        continue;
                    }
                    return Result<CliArguments, ShaderError>.Fail(
                        MissingFlagValue("/I", "an include path"));
                }

                if (flagBody.Equals("mgfx-version", StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                    if (i < args.Length)
                    {
                        string versionToken = args[i];
                        if (!int.TryParse(versionToken, out int parsedVersion) ||
                            (parsedVersion != 10 && parsedVersion != 11))
                        {
                            return Result<CliArguments, ShaderError>.Fail(new ShaderError(
                                File: "",
                                Line: 0,
                                Column: 0,
                                Code: "X0005",
                                Message: $"Invalid --mgfx-version value '{versionToken}'. Only 10 and 11 are valid."));
                        }
                        mgfxVersion = parsedVersion;
                        i++;
                        continue;
                    }
                    return Result<CliArguments, ShaderError>.Fail(
                        MissingFlagValue("--mgfx-version", "10 or 11"));
                }

                if (flagBody.StartsWith("mgfx-version:", StringComparison.OrdinalIgnoreCase))
                {
                    string versionValue = flagBody.Substring("mgfx-version:".Length);
                    if (!int.TryParse(versionValue, out int parsedVersion) ||
                        (parsedVersion != 10 && parsedVersion != 11))
                    {
                        return Result<CliArguments, ShaderError>.Fail(new ShaderError(
                            File: "",
                            Line: 0,
                            Column: 0,
                            Code: "X0005",
                            Message: $"Invalid --mgfx-version value '{versionValue}'. Only 10 and 11 are valid."));
                    }
                    mgfxVersion = parsedVersion;
                    i++;
                    continue;
                }

                // Space, ':' AND '=' forms, like every other long option here. The '=' form
                // used to fall through to the silent unknown-flag branch below, so
                // `--target-runtime=monogame-gl` compiled with the DEFAULT profile and exit
                // 0 — the wrong artifact, silently, which is exactly what X0009 exists to
                // prevent for the other value-carrying flags.
                if (TryReadValuedFlag(flagBody, "target-runtime", args, ref i, out string? runtimeValue))
                {
                    if (string.IsNullOrEmpty(runtimeValue))
                        return Result<CliArguments, ShaderError>.Fail(
                            MissingFlagValue("--target-runtime", "a runtime name"));

                    var trResult = ParseTargetRuntime(runtimeValue);
                    if (trResult.IsFailure)
                        return Result<CliArguments, ShaderError>.Fail(trResult.Error);
                    profile = trResult.Value;
                    continue;
                }

                if (flagBody.Equals("print-uniforms", StringComparison.OrdinalIgnoreCase))
                {
                    printUniforms = true;
                    i++;
                    continue;
                }

                // --input-format <auto|fx|glsl> — a NON-required escape hatch (default auto). Accepts
                // the space, ':' and '=' value forms, matching the parser's other long options.
                if (TryReadValuedFlag(flagBody, "input-format", args, ref i, out string? formatValue))
                {
                    var fmtResult = ParseInputFormat(formatValue);
                    if (fmtResult.IsFailure)
                        return Result<CliArguments, ShaderError>.Fail(fmtResult.Error);
                    inputFormat = fmtResult.Value;
                    continue;
                }

                // Unknown flag — silently ignore the flag token only. Not consuming a
                // potential following value ensures positional args are never accidentally
                // swallowed by future mgfxc flags MGCB may pass.
                i++;
                continue;
            }

            if (sourceFile is null)
                sourceFile = token;
            else if (outputFile is null)
                outputFile = token;

            i++;
        }

        if (sourceFile is null || outputFile is null)
        {
            return Result<CliArguments, ShaderError>.Fail(new ShaderError(
                File: "",
                Line: 0,
                Column: 0,
                Code: "X0003",
                Message: sourceFile is null
                    ? "Missing required argument: <SourceFile>"
                    : "Missing required argument: <OutputFile>"));
        }

        return Result<CliArguments, ShaderError>.Ok(new CliArguments(
            SourceFile: sourceFile,
            OutputFile: outputFile,
            Platform: platform,
            Debug: debug,
            IncludePaths: includePaths,
            MgfxVersion: mgfxVersion,
            DxbcBackend: dxbcBackend,
            Profile: profile,
            InputFormat: inputFormat,
            PrintUniforms: printUniforms,
            Defines: defines));
    }

    // Bug-hunt 2026-07-27 (N12): a flag that requires a value but reaches the end of the
    // argument list used to be silently ignored, so the compile ran with the DEFAULT —
    // the wrong artifact with exit 0. Loud, like every other bad-value path here.
    private static ShaderError MissingFlagValue(string flag, string expected) => new(
        File: "",
        Line: 0,
        Column: 0,
        Code: "X0009",
        Message: $"Flag '{flag}' requires a value ({expected}) but none was supplied");

    // Reads a long option that carries a value in any of the three forms the CLI accepts:
    //   --name value   (space) | --name:value (colon) | --name=value (equals)
    // Advances `i` past the consumed token(s) and returns the value when `flagBody` names this flag.
    private static bool TryReadValuedFlag(
        string flagBody, string name, string[] args, ref int i, out string? value)
    {
        if (flagBody.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            i++;
            if (i < args.Length && !IsFlag(args[i]))
            {
                value = args[i];
                i++;
            }
            else
            {
                value = string.Empty;   // missing value -> let the value parser emit a loud error
            }
            return true;
        }

        if (flagBody.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
        {
            value = flagBody.Substring(name.Length + 1);
            i++;
            return true;
        }

        if (flagBody.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
        {
            value = flagBody.Substring(name.Length + 1);
            i++;
            return true;
        }

        value = null;
        return false;
    }

    private static Result<InputFormat, ShaderError> ParseInputFormat(string? value)
    {
        InputFormat? format = (value ?? string.Empty).ToLowerInvariant() switch
        {
            "auto" => InputFormat.Auto,
            "fx"   => InputFormat.Fx,
            "glsl" => InputFormat.Glsl,
            _      => null,
        };

        if (format is null)
            return Result<InputFormat, ShaderError>.Fail(new ShaderError(
                File: "",
                Line: 0,
                Column: 0,
                Code: "X0011",
                Message: $"Invalid --input-format value '{value}'. Valid: auto, fx, glsl"));

        return Result<InputFormat, ShaderError>.Ok(format.Value);
    }

    // The flag names this CLI understands, for '/'-prefix disambiguation in IsFlag.
    // (Matched against the part of the token before any ':'.)
    private static readonly string[] KnownSlashFlagNames =
    {
        "Debug", "Profile", "I", "DxbcBackend", "mgfx-version", "target-runtime",
    };

    private static bool IsFlag(string token)
    {
        // GNU-style long options are unambiguous — no filesystem path starts with "--".
        if (token.StartsWith("--", StringComparison.Ordinal))
            return true;

        // mgfxc-style "/Opt" options collide with POSIX absolute paths, which also start
        // with '/' (e.g. "/home/user/shader.fx" on Linux/macOS). A '/'-prefixed token is
        // an option when its name (the part up to the first ':') is one of THIS CLI's
        // known flags, or — for forward compatibility with future mgfxc flags MGCB may
        // pass (e.g. "/Defines:FOO=1") — when it carries a ':' value and its name looks
        // like a bare flag (no path separator, no '.' extension). A bare extensionless
        // POSIX path like "/data" is NOT a known flag and carries no ':', so it parses
        // as positional instead of being silently dropped.
        if (token.StartsWith('/'))
        {
            int colon = token.IndexOf(':');
            string name = colon >= 0 ? token.Substring(1, colon - 1) : token.Substring(1);

            if (KnownSlashFlagNames.Any(f => f.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return true;

            return colon >= 0 && name.Length > 0 && !name.Contains('/') && !name.Contains('.');
        }

        return false;
    }

    private static string StripPrefix(string token)
    {
        if (token.StartsWith("--", StringComparison.Ordinal))
            return token.Substring(2);
        if (token.StartsWith('/'))
            return token.Substring(1);
        return token;
    }

    private static Result<PlatformTarget, ShaderError> ParseProfile(string value)
    {
        if (value.Equals("DirectX_11", StringComparison.OrdinalIgnoreCase))
            return Result<PlatformTarget, ShaderError>.Ok(PlatformTarget.DirectX);

        if (value.Equals("OpenGL", StringComparison.OrdinalIgnoreCase))
            return Result<PlatformTarget, ShaderError>.Ok(PlatformTarget.OpenGL);

        if (value.Equals("Vulkan", StringComparison.OrdinalIgnoreCase))
            return Result<PlatformTarget, ShaderError>.Ok(PlatformTarget.Vulkan);

        // Matches real mgfxc's own DirectX12ShaderProfile registration name exactly.
        if (value.Equals("DirectX_12", StringComparison.OrdinalIgnoreCase))
            return Result<PlatformTarget, ShaderError>.Ok(PlatformTarget.DirectX12);

        // FNA's D3D9 fx_2_0 effects target (.fxb). Not an mgfxc profile — additive,
        // so mgfxc drop-in parity is unaffected.
        if (value.Equals("FNA", StringComparison.OrdinalIgnoreCase))
            return Result<PlatformTarget, ShaderError>.Ok(PlatformTarget.Fna);

        if (value.Equals("PlayStation4", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("XboxOne", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Switch", StringComparison.OrdinalIgnoreCase))
        {
            return Result<PlatformTarget, ShaderError>.Fail(new ShaderError(
                File: "",
                Line: 0,
                Column: 0,
                Code: "X0010",
                Message: $"platform '{value}' is not supported by ShadowDusk"));
        }

        return Result<PlatformTarget, ShaderError>.Fail(new ShaderError(
            File: "",
            Line: 0,
            Column: 0,
            Code: "X0004",
            Message: $"Unknown profile '{value}'. Valid profiles: DirectX_11, DirectX_12, OpenGL, Vulkan, FNA"));
    }

    // Maps the friendly --target-runtime names to a proven CapabilityProfile. The profile fully
    // specifies the output target (backend + container/version), so it is set on
    // CompilerOptions.Profile and overrides /Profile and --mgfx-version.
    private static Result<CapabilityProfile, ShaderError> ParseTargetRuntime(string value)
    {
        CapabilityProfile? profile = value.ToLowerInvariant() switch
        {
            "monogame-gl"     => CapabilityProfile.MonoGameGL_3_8_2,
            "monogame-dx"     => CapabilityProfile.MonoGameDX_SM5,
            "monogame-gl-v11" => CapabilityProfile.MonoGameGL_3_8_5,
            "kni-knifx"       => CapabilityProfile.KniGL_4_02,
            "fna"             => CapabilityProfile.Fna_Fx2,
            _                 => null,
        };

        if (profile is null)
            return Result<CapabilityProfile, ShaderError>.Fail(new ShaderError(
                File: "",
                Line: 0,
                Column: 0,
                Code: "X0008",
                Message: $"Unknown --target-runtime value '{value}'. Valid: monogame-gl, monogame-dx, monogame-gl-v11, kni-knifx, fna"));

        return Result<CapabilityProfile, ShaderError>.Ok(profile);
    }
}
