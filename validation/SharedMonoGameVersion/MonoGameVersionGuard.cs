#nullable enable

using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ShadowDusk.Validation.SharedMonoGameVersion;

/// <summary>
/// Compiled into a driver only when it is built with <c>-p:MonoGameVersion=&lt;v&gt;</c>
/// (<c>validation/MonoGameVersion.targets</c>). Runs before <c>Main</c> and exits 3 unless the
/// <c>MonoGame.Framework</c> that loaded is the requested release, so a stale build of the other
/// arm can never pass as this one.
/// </summary>
internal static class MonoGameVersionGuard
{
#pragma warning disable CA2255 // A module initializer is the point: it needs no edit to each driver's Main.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Check()
    {
        string? expected = typeof(MonoGameVersionGuard).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ExpectedMonoGameVersion")?.Value;
        if (string.IsNullOrEmpty(expected))
        {
            return;
        }

        Assembly framework = typeof(Microsoft.Xna.Framework.Vector2).Assembly;
        string loaded = framework.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? framework.GetName().Version?.ToString()
            ?? "<unknown>";
        string loadedNumber = loaded.Split('+')[0];

        if (Normalize(loadedNumber) != Normalize(expected))
        {
            Console.Error.WriteLine(
                $"[MonoGameVersionGuard] FAIL: built for MonoGame {expected}, but {framework.GetName().Name} {loaded} loaded. " +
                "Rebuild with the same -p:MonoGameVersion.");
            Environment.Exit(3);
        }

        Console.WriteLine($"[MonoGameVersionGuard] MonoGame {loaded} (requested {expected})");
    }

    /// <summary><c>3.8.6</c> and <c>3.8.6.0</c> are the same release.</summary>
    private static string Normalize(string version) =>
        Version.TryParse(version, out Version? v)
            ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0)).ToString()
            : version;
}
