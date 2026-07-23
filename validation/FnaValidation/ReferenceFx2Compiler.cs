#nullable enable

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ShadowDusk.Validation.Fna;

/// <summary>
/// Arm B (reference): Microsoft's OWN fx_2_0 compiler — the system
/// <c>d3dcompiler_47.dll</c> <c>D3DCompile</c> with <c>pTarget = "fx_2_0"</c> and
/// <c>pEntrypoint = NULL</c> (entry point MUST be null for fx targets). Per
/// <c>tests/fixtures/golden/FNA/README.md</c> this is byte-identical to
/// <c>fxc.exe /T fx_2_0</c> and emits only deprecation warning X4717; it is a TEST
/// ORACLE only — it never ships in or drives the product pipeline.
///
/// PROFILE PARITY (load-bearing): ShadowDusk's FNA path compiles macro-profile passes
/// (<c>compile PS_SHADERMODEL …</c>) at ps_3_0/vs_3_0 (the documented Phase 39 profile
/// policy) and defines the macros FNA;HLSL;SM3 — never OPENGL. The corpus template's
/// <c>#if OPENGL</c> branch is what selects <c>ps_3_0</c> (its <c>#else</c> is
/// <c>ps_4_0_level_9_1</c>, which fx_2_0 rejects), so the reference arm prepends
/// <c>#define OPENGL 1</c> to compile the SAME program at the SAME profile.
/// <see cref="CheckOpenGlParity"/> proves per shader that the only OPENGL-conditional
/// content is that standard SHADERMODEL/SV_POSITION define block — if any other body
/// code were OPENGL-gated the two arms would be different programs and the shader is
/// excluded as macro-parity-unsafe instead of compared.
/// </summary>
public static class ReferenceFx2Compiler
{
    public sealed record ReferenceResult(byte[]? Bytes, string? Error, string? Warnings);

    /// <summary>
    /// Compile <paramref name="source"/> (read from <paramref name="sourcePath"/>) with
    /// the system d3dcompiler_47 at fx_2_0. Includes are inlined textually relative to
    /// the source's directory first (the corpus uses none, but stay correct), then
    /// <c>#define OPENGL 1</c> + <c>#line 1</c> are prepended for profile parity.
    /// </summary>
    public static ReferenceResult Compile(string sourcePath, string source)
    {
        string inlined = InlineIncludes(source, Path.GetDirectoryName(Path.GetFullPath(sourcePath))!, depth: 0);
        string prepared = "#define OPENGL 1\n#line 1\n" + inlined;
        byte[] bytes = Encoding.UTF8.GetBytes(prepared);

        int hr = D3DCompile(
            bytes, (nuint)bytes.Length,
            sourcePath,
            IntPtr.Zero, IntPtr.Zero,
            null,           // pEntrypoint MUST be NULL for fx_* targets
            "fx_2_0",
            0, 0,           // default flags — fxc-default optimization (never /Od; MojoShader
                            // is stricter on fxc DEBUG-style codegen, per Phase 39 notes)
            out IntPtr code, out IntPtr errors);

        string? messages = BlobToString(errors);
        ReleaseBlob(errors);

        if (hr < 0 || code == IntPtr.Zero)
        {
            ReleaseBlob(code);
            return new ReferenceResult(null, messages ?? $"D3DCompile failed with HRESULT 0x{hr:X8}", null);
        }

        byte[] fxb = BlobToArray(code);
        ReleaseBlob(code);
        return new ReferenceResult(fxb, null, messages); // messages = warnings only (X4717 expected)
    }

    // -------------------------------------------------------------------------
    // Profile-parity safety check
    // -------------------------------------------------------------------------

    private static readonly Regex DirectiveRx = new(@"^\s*#\s*(\w+)(.*)$", RegexOptions.Compiled);
    private static readonly Regex AllowedDefineRx = new(
        @"^\s*#\s*define\s+(SV_POSITION\s+POSITION|VS_SHADERMODEL\s+vs_[0-9][0-9a-z_]*|PS_SHADERMODEL\s+ps_[0-9][0-9a-z_]*)\s*(//.*)?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns null when prepending <c>#define OPENGL 1</c> provably yields the same
    /// program ShadowDusk's FNA arm compiles; otherwise a human-readable reason the
    /// shader is macro-parity-unsafe. Safe means: every <c>#if OPENGL</c>/<c>#ifdef
    /// OPENGL</c> block contains only SHADERMODEL / SV_POSITION defines (plus blanks
    /// and comments) in BOTH branches, and no other directive mentions OPENGL.
    /// Conditionals nested INSIDE the block (e.g. the <c>#if VULKAN</c> profile branch
    /// Phase 32 added to every corpus fixture) are fine — they still only ever select
    /// between those same allowed defines, which is exactly the property being proved.
    /// </summary>
    public static string? CheckOpenGlParity(string source)
    {
        string[] lines = source.Replace("\r\n", "\n").Split('\n');
        int state = 0;        // 0 = outside, 1 = inside OPENGL block (either branch)
        int otherIfDepth = 0; // non-OPENGL conditional nesting (outside OPENGL blocks)
        int nestedIfDepth = 0; // conditional nesting INSIDE an OPENGL block (e.g. #if VULKAN)

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart('﻿', ' ', '\t'); // tolerate a stray BOM

            Match m = DirectiveRx.Match(trimmed);
            bool isDirective = m.Success;
            string keyword = isDirective ? m.Groups[1].Value : "";
            string rest = isDirective ? m.Groups[2].Value : "";

            if (state == 0)
            {
                if (!isDirective)
                    continue;

                bool mentionsOpenGl = Regex.IsMatch(rest, @"\bOPENGL\b");
                switch (keyword)
                {
                    case "if" when mentionsOpenGl:
                    case "ifdef" when mentionsOpenGl:
                        if (!Regex.IsMatch(trimmed, @"^\s*#\s*(if|ifdef)\s+OPENGL\s*(//.*)?$"))
                            return $"line {i + 1}: complex OPENGL condition '{trimmed.Trim()}'";
                        state = 1;
                        nestedIfDepth = 0;
                        break;
                    case "ifndef" when mentionsOpenGl:
                    case "elif" when mentionsOpenGl:
                        return $"line {i + 1}: OPENGL used in '#{keyword}' — not the standard block shape";
                    case "if" or "ifdef" or "ifndef":
                        otherIfDepth++;
                        break;
                    case "endif" when otherIfDepth > 0:
                        otherIfDepth--;
                        break;
                    default:
                        if (mentionsOpenGl && keyword != "define")
                            return $"line {i + 1}: OPENGL referenced by '#{keyword}'";
                        break;
                }
            }
            else // inside an OPENGL-conditional block (either branch)
            {
                if (!isDirective)
                {
                    string body = trimmed.Trim();
                    if (body.Length == 0 || body.StartsWith("//", StringComparison.Ordinal)
                        || (body.StartsWith("/*", StringComparison.Ordinal) && body.EndsWith("*/", StringComparison.Ordinal)))
                        continue;
                    return $"line {i + 1}: non-define body code inside the OPENGL block: '{body}'";
                }

                switch (keyword)
                {
                    case "else":
                        break; // the #else branch must satisfy the same allowed set
                    case "endif" when nestedIfDepth > 0:
                        nestedIfDepth--;
                        break;
                    case "endif":
                        state = 0;
                        break;
                    case "define" when AllowedDefineRx.IsMatch(trimmed):
                        break;

                    // A NESTED conditional is allowed as long as it does not mention OPENGL
                    // and everything it guards is still an allowed define (the checks above
                    // and below keep enforcing that at every depth). Parity still holds: the
                    // whole block only ever selects between SHADERMODEL / SV_POSITION
                    // defines, which is the property this method exists to prove.
                    //
                    // This shape is not hypothetical — Phase 32 gave all ten corpus fixtures
                    // a `#if VULKAN` profile branch nested inside the `#else` of `#if OPENGL`,
                    // and rejecting it silently dropped those ten shaders from the FNA render
                    // gate (17/17 quietly became 7/17). Found while validating issue #145.
                    case "if" or "ifdef" or "ifndef" when !Regex.IsMatch(rest, @"\bOPENGL\b"):
                        nestedIfDepth++;
                        break;
                    case "elif" when !Regex.IsMatch(rest, @"\bOPENGL\b"):
                        break;

                    default:
                        return $"line {i + 1}: disallowed directive inside the OPENGL block: '{trimmed.Trim()}'";
                }
            }
        }

        return state == 0 ? null : "unterminated #if OPENGL block";
    }

    // -------------------------------------------------------------------------
    // Textual include inlining (corpus shaders in the run list use none; kept
    // correct for completeness — resolved relative to the including file's dir)
    // -------------------------------------------------------------------------

    private static readonly Regex IncludeRx = new(
        "^\\s*#\\s*include\\s+[\"<]([^\">]+)[\">]\\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static string InlineIncludes(string source, string dir, int depth)
    {
        if (depth > 16)
            throw new InvalidOperationException("Include nesting exceeds 16 levels — cycle?");

        return IncludeRx.Replace(source, match =>
        {
            string includePath = Path.Combine(dir, match.Groups[1].Value);
            if (!File.Exists(includePath))
                throw new FileNotFoundException($"#include not found: {includePath}");
            string text = File.ReadAllText(includePath);
            return InlineIncludes(text, Path.GetDirectoryName(Path.GetFullPath(includePath))!, depth + 1);
        });
    }

    // -------------------------------------------------------------------------
    // d3dcompiler_47 P/Invoke (ID3DBlob via raw vtable: 3 = GetBufferPointer,
    // 4 = GetBufferSize, 2 = IUnknown.Release)
    // -------------------------------------------------------------------------

    [DllImport("d3dcompiler_47.dll", ExactSpelling = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int D3DCompile(
        byte[] pSrcData,
        nuint srcDataSize,
        [MarshalAs(UnmanagedType.LPStr)] string? pSourceName,
        IntPtr pDefines,
        IntPtr pInclude,
        [MarshalAs(UnmanagedType.LPStr)] string? pEntrypoint,
        [MarshalAs(UnmanagedType.LPStr)] string pTarget,
        uint flags1,
        uint flags2,
        out IntPtr ppCode,
        out IntPtr ppErrorMsgs);

    private static unsafe byte[] BlobToArray(IntPtr blob)
    {
        void** vtbl = *(void***)blob;
        var getBufferPointer = (delegate* unmanaged[Stdcall]<IntPtr, void*>)vtbl[3];
        var getBufferSize = (delegate* unmanaged[Stdcall]<IntPtr, nuint>)vtbl[4];
        nuint size = getBufferSize(blob);
        byte[] result = new byte[size];
        Marshal.Copy((IntPtr)getBufferPointer(blob), result, 0, (int)size);
        return result;
    }

    private static unsafe string? BlobToString(IntPtr blob)
    {
        if (blob == IntPtr.Zero)
            return null;
        void** vtbl = *(void***)blob;
        var getBufferPointer = (delegate* unmanaged[Stdcall]<IntPtr, void*>)vtbl[3];
        string? s = Marshal.PtrToStringAnsi((IntPtr)getBufferPointer(blob));
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static unsafe void ReleaseBlob(IntPtr blob)
    {
        if (blob == IntPtr.Zero)
            return;
        void** vtbl = *(void***)blob;
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2];
        release(blob);
    }
}
