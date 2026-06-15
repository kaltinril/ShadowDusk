#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.Cli;

internal static class ArgumentParser
{
    public static string UsageText { get; } =
        """
        Usage: ShadowDuskCLI <SourceFile> <OutputFile> [options]

        Options:
          /Profile:<Platform>       Target platform. Default: DirectX_11
                                    Platforms: DirectX_11, OpenGL, Vulkan, FNA
          /Debug                    Include debug information in output
          /I <path>                 Additional include search path (repeatable)
          /DxbcBackend:<Backend>    DXBC backend for DirectX_11. Default: vkd3d
                                    (cross-platform). d3dcompiler is the Windows-only
                                    correctness oracle; never required for correct output.
          --mgfx-version <10|11>    Output format version. Default: 10
          --target-runtime <name>   Output target runtime (picks backend + format together).
                                    Names: monogame-gl, monogame-dx, monogame-gl-v11,
                                    kni-knifx, fna. Overrides /Profile and --mgfx-version.

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
                    }
                    continue;
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
                    }
                    continue;
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

                if (flagBody.Equals("target-runtime", StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                    if (i < args.Length)
                    {
                        var trResult = ParseTargetRuntime(args[i]);
                        if (trResult.IsFailure)
                            return Result<CliArguments, ShaderError>.Fail(trResult.Error);
                        profile = trResult.Value;
                        i++;
                    }
                    continue;
                }

                if (flagBody.StartsWith("target-runtime:", StringComparison.OrdinalIgnoreCase))
                {
                    var trResult = ParseTargetRuntime(flagBody.Substring("target-runtime:".Length));
                    if (trResult.IsFailure)
                        return Result<CliArguments, ShaderError>.Fail(trResult.Error);
                    profile = trResult.Value;
                    i++;
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
            Profile: profile));
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
            Message: $"Unknown profile '{value}'. Valid profiles: DirectX_11, OpenGL, Vulkan, FNA"));
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
