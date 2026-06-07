#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.Cli;

internal static class ArgumentParser
{
    public static string UsageText { get; } =
        """
        Usage: mgfxc <SourceFile> <OutputFile> [options]

        Options:
          /Profile:<Platform>       Target platform. Default: DirectX_11
                                    Platforms: DirectX_11, OpenGL, Vulkan
          /Debug                    Include debug information in output
          /I <path>                 Additional include search path (repeatable)
          --mgfx-version <10|11>    Output format version. Default: 10

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
            MgfxVersion: mgfxVersion));
    }

    private static bool IsFlag(string token)
    {
        // GNU-style long options are unambiguous — no filesystem path starts with "--".
        if (token.StartsWith("--", StringComparison.Ordinal))
            return true;

        // mgfxc-style "/Opt" options collide with POSIX absolute paths, which also start
        // with '/' (e.g. "/home/user/shader.fx" on Linux/macOS). Treat a '/'-prefixed
        // token as an option ONLY when its name (the part up to the first ':') looks like a
        // bare flag — no path separator and no '.' extension. That way "/Profile:OpenGL",
        // "/Debug" and "/I:/usr/include" are options, while an absolute source/output path
        // like "/home/runner/work/.../Grayscale.fx" is correctly parsed as positional.
        if (token.StartsWith('/'))
        {
            int colon = token.IndexOf(':');
            string name = colon >= 0 ? token.Substring(1, colon - 1) : token.Substring(1);
            return name.Length > 0 && !name.Contains('/') && !name.Contains('.');
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
            Message: $"Unknown profile '{value}'. Valid profiles: DirectX_11, OpenGL, Vulkan"));
    }
}
