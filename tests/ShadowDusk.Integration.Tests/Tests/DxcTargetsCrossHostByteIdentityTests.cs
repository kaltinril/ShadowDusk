#nullable enable

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL.Dxc;
using Vortice.Dxc;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// The cross-host byte-identity check for the two targets whose shipped bytecode comes
/// straight out of DXC: <b>Vulkan</b> (SPIR-V) and <b>DirectX 12</b> (DXIL), over the WHOLE
/// fixture corpus (every <c>.fx</c> under <c>tests/fixtures/shaders</c>, which contains the
/// <see cref="CrossHostByteIdentityTests"/> corpus). Every outcome is pinned against ONE
/// committed manifest, <c>tests/fixtures/golden/byte-identity/dxc-targets-manifest.json</c>,
/// generated on win-x64.
///
/// <para><b>Why it exists.</b> Each desktop host loads a different DXC binary: Vortice.Dxc's
/// win-x64 natives (<c>dxcoob 1.7.2212.40 (e043f4a12)</c>), Vortice.Dxc's linux-x64 natives
/// (which identify as <c>dxc(private) 1.7.0.3759 (8c9d92be7)</c>, DXC tag <c>v1.7.2212</c>, 28
/// commits behind <c>e043f4a1</c>), and our own macOS build of <c>e043f4a1</c>. The OpenGL arm
/// of <see cref="CrossHostByteIdentityTests"/> sees DXC's SPIR-V only after SPIRV-Cross and the
/// managed rewrite; this class pins DXC's own output for the targets that ship it.</para>
///
/// <para><b>What is compared.</b></para>
/// <list type="bullet">
/// <item><b>Vulkan</b>: the <c>.mgfx</c> bytes and every SPIR-V module DXC returned, raw.</item>
/// <item><b>DirectX 12</b>: three keys. <c>mgfx</c> is the raw <c>.mgfx</c> and is asserted only
/// on Windows: a DXIL container differs by construction on every other host, because (1) its
/// container digest is the <c>dxil.dll</c> signature, which only Windows produces (unsigned,
/// <c>SD0214</c>, elsewhere), and (2) the DXIL bitcode embeds the compiler's own identity
/// string (<c>!llvm.ident</c>, and the debug information's <c>producer</c>), so the <c>HASH</c>
/// part (a hash of that bitcode), the PDB name derived from it, and the container's size
/// follow. <c>dxil</c> is a digest of every DXIL container's
/// <see cref="DxilCanonicalText">disassembly</see> with exactly those identity facts removed,
/// and <c>mgfxNormalized</c> is the <c>.mgfx</c> with each DXIL container replaced by that
/// digest and the two fields that follow the container's bytes zeroed (its shader-record
/// length and the header's effect key, an MD5 of the body), so the rest of the effect is
/// pinned too.</item>
/// <item>Both: the warning list, and for a fixture that does not compile, its error list. A
/// fixture that compiles on one host and fails on another is a difference like any other. The
/// two unsigned-DXIL notices (<c>SD0214</c> and DXC's own) are left out: every non-Windows host
/// emits them by construction.</item>
/// <item>Each target twice: release, and with <see cref="CompilerOptions.Debug"/>.</item>
/// </list>
///
/// <para><b>Regenerating</b>: <c>SHADOWDUSK_REGENERATE_BYTE_MANIFEST=1</c> on win-x64, exactly as
/// <see cref="CrossHostByteIdentityTests"/>. <c>SHADOWDUSK_BYTE_IDENTITY_DUMP=&lt;dir&gt;</c>
/// writes every fixture's SPIR-V modules, DXIL containers and DXIL disassembly there (the CI
/// integration lane sets it and uploads the directory on failure), so a difference can be
/// diffed instead of guessed at.</para>
///
/// <para><b>Honesty rule</b>, as for the other manifests: a mismatch is a real per-host
/// difference in the DXC native. Never regenerate per host, and never widen the normalization
/// beyond the host-identity facts named above.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class DxcTargetsCrossHostByteIdentityTests
{
    private const string RegenerateEnvVar = "SHADOWDUSK_REGENERATE_BYTE_MANIFEST";
    private const string DumpEnvVar = "SHADOWDUSK_BYTE_IDENTITY_DUMP";
    private const string ManifestName = "dxc-targets-manifest.json";
    private const int MinimumExpectedFixtures = 150;

    private static readonly TimeSpan CorpusBudget = TimeSpan.FromMinutes(10);

    private static string ShadersRoot => Path.Combine(AppContext.BaseDirectory, "fixtures", "shaders");

    [DxcFact]
    public Task Vulkan_MatchesCommittedManifest() => RunAsync("Vulkan", PlatformTarget.Vulkan);

    [DxcFact]
    public Task DirectX12_MatchesCommittedManifest() => RunAsync("DirectX12", PlatformTarget.DirectX12);

    // The same corpus with CompilerOptions.Debug: DXC's debug information is where it emits
    // OpString literals (the SPIR-V builder's string-literal map, DXC #5002) and the DXIL debug
    // metadata, neither of which a release compile reaches.
    [DxcFact]
    public Task VulkanDebug_MatchesCommittedManifest() => RunAsync("Vulkan.Debug", PlatformTarget.Vulkan, debug: true);

    [DxcFact]
    public Task DirectX12Debug_MatchesCommittedManifest() => RunAsync("DirectX12.Debug", PlatformTarget.DirectX12, debug: true);

    [Fact]
    public void DxilCanonicalText_RemovesOnlyTheHostIdentityFacts()
    {
        const string windows =
            "; shader debug name: 0123456789abcdef0123456789abcdef.pdb\n" +
            "; shader hash: 0123456789abcdef0123456789abcdef\n" +
            ";\n" +
            "define void @main() {\n" +
            "  ret void\n" +
            "}\n" +
            "!llvm.ident = !{!0}\n" +
            "!dx.version = !{!1}\n" +
            "!0 = !{!\"dxcoob 1.7.2212.40 (e043f4a12)\"}\n" +
            "!1 = !{i32 1, i32 0}\n" +
            "!2 = distinct !DICompileUnit(language: DW_LANG_C_plus_plus, producer: \"dxcoob 1.7.2212.40 (e043f4a12)\")\n";
        const string linux =
            "; shader debug name: fedcba9876543210fedcba9876543210.pdb\n" +
            "; shader hash: fedcba9876543210fedcba9876543210\n" +
            ";\n" +
            "define void @main() {\n" +
            "  ret void\n" +
            "}\n" +
            "!llvm.ident = !{!0}\n" +
            "!dx.version = !{!1}\n" +
            "!0 = !{!\"dxc(private) 1.7.0.3759 (8c9d92be7)\"}\n" +
            "!1 = !{i32 1, i32 0}\n" +
            "!2 = distinct !DICompileUnit(language: DW_LANG_C_plus_plus, producer: \"dxc(private) 1.7.0.3759 (8c9d92be7)\")\n";

        DxilCanonicalText(linux).ShouldBe(DxilCanonicalText(windows));
        DxilCanonicalText(windows).ShouldContain("ret void", Case.Sensitive);
        DxilCanonicalText(windows).ShouldContain("!1 = !{i32 1, i32 0}", Case.Sensitive);
        DxilCanonicalText(windows.Replace("ret void", "ret void ; changed", StringComparison.Ordinal))
            .ShouldNotBe(DxilCanonicalText(windows), "anything but the identity facts must still reach the digest");
    }

    // -------------------------------------------------------------------------

    private static async Task RunAsync(string targetKey, PlatformTarget target, bool debug = false)
    {
        using var cts = new CancellationTokenSource(CorpusBudget);
        IReadOnlyList<string> fixtures = Corpus();
        fixtures.Count.ShouldBeGreaterThanOrEqualTo(MinimumExpectedFixtures, "the whole fixture corpus must reach this check");
        var resolver = new InMemoryIncludeResolver(AllSources());

        var actual = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
        foreach (string fx in fixtures)
        {
            (Entry entry, Artifacts art) = await CompileAsync(fx, target, debug, resolver, cts.Token);
            actual[$"{targetKey}/{fx}"] = entry;
            // With the dump directory set, every fixture's DXC output is written there, so
            // two hosts' dumps can be diffed file by file.
            Dump($"{targetKey}/{fx}", art);
        }

        if (Environment.GetEnvironmentVariable(RegenerateEnvVar) == "1")
        {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                throw new InvalidOperationException($"{RegenerateEnvVar}=1 is set in CI; regeneration asserts nothing.");
            if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
                throw new InvalidOperationException($"{RegenerateEnvVar}=1 requires win-x64, the manifest's canonical host.");
            RegenerateSection(targetKey, actual);
            return;
        }

        var manifest = JsonSerializer.Deserialize<Dictionary<string, Entry>>(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", "byte-identity", ManifestName), cts.Token))!;
        var expected = manifest.Where(kv => kv.Key.StartsWith(targetKey + "/", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        // The raw DirectX 12 container is asserted on Windows only (see the class remarks).
        bool rawDxilComparable = target != PlatformTarget.DirectX12 || OperatingSystem.IsWindows();

        var discrepancies = new List<string>();
        foreach ((string key, Entry e) in actual)
        {
            if (!expected.TryGetValue(key, out Entry? x))
            {
                discrepancies.Add($"UNTRACKED {key}: not in {ManifestName}");
                continue;
            }

            var fields = new List<string>();
            if (!SameList(e.Errors, x.Errors))
                fields.Add($"errors manifest=[{Join(x.Errors)}] this-host=[{Join(e.Errors)}]");
            if (rawDxilComparable && e.Mgfx != x.Mgfx)
                fields.Add($"mgfx manifest={x.Mgfx} this-host={e.Mgfx}");
            if (e.MgfxNormalized != x.MgfxNormalized)
                fields.Add($"mgfxNormalized manifest={x.MgfxNormalized} this-host={e.MgfxNormalized}");
            if (e.Spirv != x.Spirv)
                fields.Add($"spirv manifest={x.Spirv} this-host={e.Spirv}");
            if (e.Dxil != x.Dxil)
                fields.Add($"dxil manifest={x.Dxil} this-host={e.Dxil}");
            if (!SameList(e.Warnings, x.Warnings))
                fields.Add($"warnings manifest=[{Join(x.Warnings)}] this-host=[{Join(e.Warnings)}]");

            if (fields.Count > 0)
                discrepancies.Add($"MISMATCH  {key}: {string.Join("; ", fields)}");
        }

        foreach (string key in expected.Keys.Where(k => !actual.ContainsKey(k)))
            discrepancies.Add($"MISSING   {key}: in {ManifestName} but not in the corpus");

        discrepancies.ShouldBeEmpty(
            $"every '{targetKey}' outcome on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}) " +
            $"must equal the committed win-x64 {ManifestName}. A mismatch is a real difference in this host's DXC " +
            $"native; never regenerate per host. {discrepancies.Count} of {actual.Count} differ:\n" +
            string.Join("\n", discrepancies));

        // Guards the guard: the corpus must actually exercise compiled output, not only errors.
        actual.Values.Count(v => v.Errors is null).ShouldBeGreaterThan(100, $"most of the corpus compiles for {targetKey}");
    }

    private static IReadOnlyList<string> Corpus() =>
        Directory.EnumerateFiles(ShadersRoot, "*.fx", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(ShadersRoot, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>Every fixture file, LF-normalized, keyed by its forward-slash relative path.</summary>
    private static Dictionary<string, string> AllSources() =>
        Directory.EnumerateFiles(ShadersRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(
                p => Path.GetRelativePath(ShadersRoot, p).Replace('\\', '/'),
                p => File.ReadAllText(p).Replace("\r\n", "\n", StringComparison.Ordinal),
                StringComparer.Ordinal);

    private static async Task<(Entry, Artifacts)> CompileAsync(
        string fx, PlatformTarget target, bool debug, IIncludeResolver resolver, CancellationToken ct)
    {
        string source = (await File.ReadAllTextAsync(Path.Combine(ShadersRoot, fx), ct))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var blobs = new ConcurrentQueue<PlatformBlob>();
        var compiler = new EffectCompiler(dxcCompilerFactory: () => new RecordingDxc(new DxcShaderCompiler(), blobs));
        var result = await compiler.CompileAsync(source, new CompilerOptions
        {
            Target = target,
            Debug = debug,
            // Host-independent: a fixed relative name (MGFX v11 stores it per shader), and
            // includes served from memory under the same relative names.
            SourceFileName = fx,
            IncludeResolver = resolver,
            // MinimalWithInclude.fx's header lives in the sibling includes/ directory (the /I case).
            AdditionalIncludePaths = ["includes"],
        }, ct);

        if (result.IsFailure)
        {
            return (new Entry { Errors = result.Error.Where(e => !IsSigningNotice(e)).Select(e => e.FxcFormattedMessage).ToArray() }, new Artifacts([], [], []));
        }

        byte[] mgfx = result.Value.Data;
        string[] warnings = result.Value.Warnings
            .Where(w => !IsSigningNotice(w))
            .Select(w => w.FxcFormattedMessage)
            .ToArray();

        if (target == PlatformTarget.Vulkan)
        {
            byte[][] spirv = blobs.Where(b => b.Kind == BlobKind.Spirv).Select(b => b.Bytes.ToArray()).ToArray();
            spirv.ShouldNotBeEmpty($"{fx}: no SPIR-V was recorded");
            return (new Entry
            {
                Mgfx = Hex(SHA256.HashData(mgfx)),
                Spirv = Digest(spirv.Select(s => Hex(SHA256.HashData(s)))),
                Warnings = warnings,
            }, new Artifacts(spirv, [], []));
        }

        // DirectX 12: every DXIL container DXC returned (the shipped ones and any reflection
        // companion), canonicalized; then the .mgfx with each shipped container replaced by
        // its canonical digest.
        var dxil = blobs.Where(b => b.Kind == BlobKind.Dxil).Select(b => b.Bytes.ToArray()).Distinct(ByteArrayComparer.Instance).ToArray();
        dxil.ShouldNotBeEmpty($"{fx}: no DXIL was recorded");
        string[] texts = dxil.Select(Disassemble).ToArray();
        string[] canonical = texts.Select(t => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(DxilCanonicalText(t))))).ToArray();

        // The MGFX header's effect key (bytes 6..9) is an MD5 of the body, so it moves with the
        // containers too; the body it is derived from is what the normalized digest pins.
        byte[] normalized = mgfx.ToArray();
        Encoding.ASCII.GetString(normalized, 0, 4).ShouldBe("MGFX", $"{fx}: not an MGFX container");
        normalized.AsSpan(6, 4).Clear();
        int replaced = 0;
        int lengths = 0;
        for (int i = 0; i < dxil.Length; i++)
        {
            (normalized, int n, int l) = ReplaceAll(normalized, dxil[i], Encoding.ASCII.GetBytes(canonical[i]));
            replaced += n;
            lengths += l;
        }

        replaced.ShouldBeGreaterThan(0, $"{fx}: no DXIL container DXC returned was found verbatim in the .mgfx");
        lengths.ShouldBe(replaced, $"{fx}: every shipped DXIL container must sit in a DirectX 12 shader record whose length was normalized");
        // A leftover DXBC container would make mgfxNormalized host-dependent, and silently so.
        IndexOf(normalized, "DXBC"u8.ToArray(), 0).ShouldBe(-1, $"{fx}: a DXIL container in the .mgfx was not one DXC returned");

        return (new Entry
        {
            Mgfx = Hex(SHA256.HashData(mgfx)),
            MgfxNormalized = Hex(SHA256.HashData(normalized)),
            Dxil = Digest(canonical),
            Warnings = warnings,
        }, new Artifacts([], dxil, texts));
    }

    /// <summary>
    /// The two notices that say a DirectX 12 compile was not signed, which every non-Windows
    /// host emits by construction: ShadowDusk's own <c>SD0214</c>, and DXC's own warning (DXC
    /// reports it when no <c>dxil.dll</c> validator is bound). Matched exactly, so no other
    /// diagnostic can hide behind the filter.
    /// </summary>
    private static bool IsSigningNotice(ShaderError e) =>
        e.Severity == ShaderErrorSeverity.Warning
        && (e.Code == "SD0214"
            || e.Message.Equals(DxcUnsignedWarning, StringComparison.Ordinal)
            || e.Message.Equals("warning: " + DxcUnsignedWarning, StringComparison.Ordinal));

    private const string DxcUnsignedWarning =
        "DXIL.dll not found.  Resulting DXIL will not be signed for use in release environments.";

    /// <summary>
    /// The DXIL disassembly with the host-identity facts removed, and nothing else: the
    /// <c>; shader hash:</c> line (the <c>HASH</c> part, a hash over bitcode that embeds the
    /// compiler identity) and, under <c>Debug</c>, the <c>; shader debug name:</c> line (DXC names
    /// the PDB after that same hash), the <c>!llvm.ident</c> named node plus the one metadata node it points
    /// at (the compiler's version string), and every other occurrence of that exact string (the
    /// debug information's <c>producer:</c>), which is replaced by a fixed token. The container
    /// signature is not in the disassembly at all.
    /// </summary>
    internal static string DxilCanonicalText(string disassembly)
    {
        string[] lines = disassembly.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Match ident = lines.Select(l => Regex.Match(l, @"^!llvm\.ident = !\{(![0-9]+)\}$")).FirstOrDefault(m => m.Success) ?? Match.Empty;
        string? identNode = ident.Success ? ident.Groups[1].Value + " = " : null;
        string? identity = identNode is null
            ? null
            : lines.Select(l => Regex.Match(l, "^" + Regex.Escape(identNode) + @"!\{!(""[^""]+"")\}$"))
                .FirstOrDefault(m => m.Success)?.Groups[1].Value;

        return string.Join("\n", lines
            .Where(l =>
                !l.StartsWith("; shader hash:", StringComparison.Ordinal)
                && !l.StartsWith("; shader debug name:", StringComparison.Ordinal)
                && !l.StartsWith("!llvm.ident = ", StringComparison.Ordinal)
                && (identNode is null || !l.StartsWith(identNode, StringComparison.Ordinal)))
            .Select(l => identity is null ? l : l.Replace(identity, "\"<dxc identity>\"", StringComparison.Ordinal)));
    }

    private static string Disassemble(byte[] container)
    {
        HLSL.Dxc.DxcLoader.Register().ShouldBeNull("the pinned DXC must be loaded to disassemble DXIL");
        using IDxcCompiler3 compiler = Vortice.Dxc.Dxc.CreateDxcCompiler<IDxcCompiler3>();
        GCHandle pin = GCHandle.Alloc(container, GCHandleType.Pinned);
        try
        {
            var buffer = new DxcBuffer
            {
                Ptr = pin.AddrOfPinnedObject(),
                Size = (nint)container.Length,
                Encoding = 0,
            };
            using IDxcResult result = compiler.Disassemble<IDxcResult>(in buffer);
            result.GetStatus().Success.ShouldBeTrue("DXC must disassemble its own DXIL");
            using IDxcBlob text = result.GetOutput(DxcOutKind.Disassembly);
            return Encoding.UTF8.GetString(text.AsBytes()).TrimEnd('\0');
        }
        finally
        {
            pin.Free();
        }
    }

    private static void Dump(string key, Artifacts art)
    {
        string? dir = Environment.GetEnvironmentVariable(DumpEnvVar);
        if (string.IsNullOrEmpty(dir))
            return;

        // Per RID and runtime: CI runs the net8.0 and net10.0 hosts of this assembly concurrently.
        string stem = Path.Combine(dir, $"{RuntimeInformation.RuntimeIdentifier}-net{Environment.Version.Major}", key.Replace('/', '_'));
        Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
        for (int i = 0; i < art.Spirv.Length; i++)
            File.WriteAllBytes($"{stem}.{i}.spv", art.Spirv[i]);
        for (int i = 0; i < art.Dxil.Length; i++)
        {
            File.WriteAllBytes($"{stem}.{i}.dxil", art.Dxil[i]);
            File.WriteAllText($"{stem}.{i}.dxil.txt", art.DxilText[i]);
        }
    }

    private static void RegenerateSection(string targetKey, SortedDictionary<string, Entry> entries)
    {
        string path = Path.Combine(FindRepoRoot(), "tests", "fixtures", "golden", "byte-identity", ManifestName);
        var merged = new SortedDictionary<string, Entry>(
            (File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) : null)
                ?? new Dictionary<string, Entry>(),
            StringComparer.Ordinal);
        foreach (string stale in merged.Keys.Where(k => k.StartsWith(targetKey + "/", StringComparison.Ordinal)).ToList())
            merged.Remove(stale);
        foreach ((string key, Entry entry) in entries)
            merged[key] = entry;

        string json = JsonSerializer.Serialize(merged, JsonOptions);
        File.WriteAllText(path, json.ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Could not locate the repo root (ShadowDusk.slnx).");
    }

    /// <summary>
    /// Replaces every occurrence of a DXIL container with <paramref name="replacement"/>, and
    /// zeroes the MGFX shader-record length in front of it: that length is
    /// <c>12 + container length</c> (<see cref="DirectX12ShaderCodeWrapper"/>'s magic and two
    /// slot ints, then the container), so it moves with the container's size, which the
    /// compiler-identity string changes by construction.
    /// </summary>
    private static (byte[], int, int) ReplaceAll(byte[] haystack, byte[] needle, byte[] replacement)
    {
        const uint WrapperMagic = 0xB00B00;
        var output = new List<byte>(haystack.Length);
        int count = 0;
        int lengths = 0;
        int start = 0;
        for (int at = IndexOf(haystack, needle, 0); at >= 0; at = IndexOf(haystack, needle, start))
        {
            output.AddRange(haystack.AsSpan(start, at - start).ToArray());
            if (at - start >= 16
                && BitConverter.ToUInt32(haystack, at - 12) == WrapperMagic
                && BitConverter.ToInt32(haystack, at - 16) == needle.Length + 12)
            {
                for (int i = output.Count - 16; i < output.Count - 12; i++)
                    output[i] = 0;
                lengths++;
            }

            output.AddRange(replacement);
            start = at + needle.Length;
            count++;
        }

        output.AddRange(haystack.AsSpan(start).ToArray());
        return (output.ToArray(), count, lengths);
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        int i = haystack.AsSpan(from).IndexOf(needle);
        return i < 0 ? -1 : from + i;
    }

    private static bool SameList(string[]? a, string[]? b) =>
        (a is null && b is null) || (a is not null && b is not null && a.SequenceEqual(b, StringComparer.Ordinal));

    private static string Join(string[]? list) => list is null ? "<none>" : string.Join(" | ", list);

    private static string Digest(IEnumerable<string> hashes) =>
        Hex(SHA256.HashData(Encoding.ASCII.GetBytes(string.Join("\n", hashes.OrderBy(h => h, StringComparer.Ordinal)))));

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>One fixture x target outcome. Null members are absent from the JSON.</summary>
    internal sealed class Entry
    {
        [JsonPropertyName("errors")] public string[]? Errors { get; set; }
        [JsonPropertyName("mgfx")] public string? Mgfx { get; set; }
        [JsonPropertyName("mgfxNormalized")] public string? MgfxNormalized { get; set; }
        [JsonPropertyName("spirv")] public string? Spirv { get; set; }
        [JsonPropertyName("dxil")] public string? Dxil { get; set; }
        [JsonPropertyName("warnings")] public string[]? Warnings { get; set; }
    }

    private sealed record Artifacts(byte[][] Spirv, byte[][] Dxil, string[] DxilText);

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj) => obj.Length;
    }

    private sealed class RecordingDxc(DxcShaderCompiler inner, ConcurrentQueue<PlatformBlob> blobs) : IDxcShaderCompiler, IDisposable
    {
        public async Task<Result<PlatformBlob, ShaderError>> CompileAsync(DxcCompileRequest request, CancellationToken cancellationToken = default) =>
            Record(await inner.CompileAsync(request, cancellationToken).ConfigureAwait(false));

        public Result<PlatformBlob, ShaderError> Compile(DxcCompileRequest request, CancellationToken cancellationToken = default) =>
            Record(inner.Compile(request, cancellationToken));

        public Result<string, ShaderError> Preprocess(DxcPreprocessRequest request, CancellationToken cancellationToken = default) =>
            inner.Preprocess(request, cancellationToken);

        public void Dispose() => inner.Dispose();

        private Result<PlatformBlob, ShaderError> Record(Result<PlatformBlob, ShaderError> result)
        {
            if (result.IsSuccess)
                blobs.Enqueue(result.Value);
            return result;
        }
    }
}
