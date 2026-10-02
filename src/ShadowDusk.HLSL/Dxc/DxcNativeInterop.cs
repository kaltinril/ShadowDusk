#nullable enable

using System.Runtime.InteropServices;
using System.Text;
using ShadowDusk.Core;
using SharpGen.Runtime;
using Vortice.Dxc;

namespace ShadowDusk.HLSL.Dxc;

/// <summary>
/// Invokes <c>IDxcCompiler3::Compile</c> through the raw COM vtable with the argument
/// strings encoded in the platform's native <c>wchar_t</c> width.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists (Phase 37 Finding B — the Linux "Internal Compiler error").</b>
/// DXC's C API declares the argument array as <c>LPCWSTR*</c>. On Windows that is
/// UTF-16 (<c>wchar_t</c> == 2 bytes), but DXC's non-Windows builds compile
/// <c>WinAdapter.h</c> with the platform's native <c>wchar_t</c>, which is
/// <b>4 bytes (UTF-32) on Linux and macOS</b>. Vortice.Dxc 3.3.4's managed wrapper
/// (<c>Interop.AllocToPointers</c>) marshals every argument with
/// <c>Marshal.StringToHGlobalUni</c> — UTF-16 — on <i>every</i> OS, so the Linux
/// <c>libdxcompiler.so</c> reads garbage arguments and fails <i>every</i> compile with
/// <c>0x80AA000C</c> / <c>"Internal Compiler error:"</c> (empty message).
/// </para>
/// <para>
/// Proven by a single-factor toggle against the same pinned native in a clean
/// <c>mcr.microsoft.com/dotnet/sdk:8.0</c> container: identical shader + flags, raw
/// vtable call — UTF-16 arguments reproduce the ICE; UTF-32 arguments compile
/// successfully. See <c>plan/DONE/PHASE-37-cross-platform-native-availability.md</c>.
/// </para>
/// <para>
/// This helper performs the exact same vtable call Vortice's generated code makes
/// (slot 3 on <see cref="IDxcCompiler3"/>), differing only in argument encoding
/// (platform <c>wchar_t</c>) and source-buffer encoding (explicit UTF-8 /
/// <c>DXC_CP_UTF8</c> rather than ANSI / <c>DXC_CP_ACP</c>, which is codepage-dependent
/// on Windows and therefore non-deterministic for non-ASCII sources). The native DXC
/// binary is unchanged — same pinned Vortice.Dxc 3.3.4 <c>dxcompiler</c> everywhere.
/// </para>
/// </remarks>
internal static unsafe class DxcNativeInterop
{
    /// <summary>Win32 code-page constant for UTF-8, used by DXC as <c>DXC_CP_UTF8</c>.</summary>
    private const uint DxcCpUtf8 = 65001;

    /// <summary>Vtable slot of <c>IDxcCompiler3::Compile</c> (after the 3 IUnknown slots).</summary>
    private const int CompileVtblSlot = 3;

    /// <summary>Native layout of DXC's <c>DxcBuffer</c> (<c>{ const void* Ptr; SIZE_T Size; UINT32 Encoding; }</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeDxcBuffer
    {
        public nint Ptr;
        public nuint Size;
        public uint Encoding;
    }

    /// <summary>
    /// Compiles <paramref name="source"/> with <paramref name="arguments"/>, returning the
    /// <see cref="IDxcResult"/> exactly as <c>IDxcCompiler3.Compile</c> would.
    /// </summary>
    /// <param name="compiler">The live DXC compiler instance.</param>
    /// <param name="source">HLSL source text (passed to DXC as UTF-8).</param>
    /// <param name="arguments">Command-line style DXC arguments.</param>
    /// <param name="includeHandler">
    /// Optional include handler. Note: if DXC invokes the handler off-Windows, the file
    /// name reaches the managed callback through SharpGen's UTF-16 unmarshalling and would
    /// be garbled by the same <c>wchar_t</c> mismatch — ShadowDusk flattens
    /// <c>#include</c>s in its own preprocessor before DXC, so the callback never fires
    /// on the product pipeline.
    /// </param>
    public static IDxcResult Compile(
        IDxcCompiler3 compiler,
        string source,
        IReadOnlyList<string> arguments,
        IDxcIncludeHandler? includeHandler)
    {
        // Off Windows, take DXC's one-time LLVM signal-handler registration out of the
        // process before any real compile can race it (see DxcSignalIsolation).
        DxcSignalIsolation.EnsureIsolated(compiler);
        return CompileRaw(compiler, source, arguments, includeHandler);
    }

    /// <summary>
    /// The raw vtable call behind <see cref="Compile"/>, without the signal isolation step
    /// (which itself compiles through here).
    /// </summary>
    /// <remarks>
    /// Runs on a <see cref="NativeCompileStack"/> worker (issue #306): DXC recurses once per
    /// nesting level of the source, and on the caller's own stack a valid but very deep shader
    /// overflowed it inside <c>dxcompiler</c> and killed the host process with no diagnostic.
    /// Every in-process DXC compile ShadowDusk makes comes through here: SPIR-V, DXIL, the
    /// reflection companion compile, <c>-P</c> preprocessing and the signal-isolation prime.
    /// The calling thread blocks until the worker returns, so what the caller serializes
    /// (<see cref="DxcSignalIsolation"/>'s lock) stays serialized, and <see cref="DxcForkGate"/>
    /// is entered on the worker, the thread that is actually inside DXC's <c>setlocale</c>.
    /// </remarks>
    internal static IDxcResult CompileRaw(
        IDxcCompiler3 compiler,
        string source,
        IReadOnlyList<string> arguments,
        IDxcIncludeHandler? includeHandler) =>
        NativeCompileStack.Run(() => CompileRawOnThisThread(compiler, source, arguments, includeHandler));

    private static IDxcResult CompileRawOnThisThread(
        IDxcCompiler3 compiler,
        string source,
        IReadOnlyList<string> arguments,
        IDxcIncludeHandler? includeHandler)
    {
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);

        int argCount = arguments.Count;
        nint[] argPointers = new nint[argCount];
        nint argArray = 0;

        try
        {
            for (int i = 0; i < argCount; i++)
                argPointers[i] = AllocNativeWideString(arguments[i]);

            argArray = Marshal.AllocHGlobal(nint.Size * Math.Max(argCount, 1));
            Marshal.Copy(argPointers, 0, argArray, argCount);

            nint includeHandlerPtr = includeHandler is null
                ? 0
                : MarshallingHelpers.ToCallbackPtr<IDxcIncludeHandler>(includeHandler);

            fixed (byte* pSource = sourceBytes)
            {
                var buffer = new NativeDxcBuffer
                {
                    Ptr      = (nint)pSource,
                    Size     = (nuint)sourceBytes.Length,
                    Encoding = DxcCpUtf8,
                };

                Guid iid = typeof(IDxcResult).GUID;
                nint resultPtr = 0;

                // Same shape as Vortice's generated call:
                // HRESULT Compile(const DxcBuffer*, LPCWSTR* pArguments, UINT32 argCount,
                //                 IDxcIncludeHandler*, REFIID, LPVOID* ppResult)
                var compile = (delegate* unmanaged[Stdcall]<nint, void*, void*, int, void*, void*, void*, int>)
                    ((nint*)*(nint*)compiler.NativePointer)[CompileVtblSlot];

                // macOS: no fork() may run while DXC is inside setlocale (see DxcForkGate).
                // The gate covers ONLY this direct native call: every setlocale DXC makes is
                // inside IDxcCompiler3::Compile (measured), and nothing in here can dlopen/dlsym
                // (a first-call P/Invoke bind would), which fork() blocks while it waits. That
                // includes DXC's own validator lookup: at the pinned commit dlopen("libdxil")
                // runs once, in the library constructor (DllMain -> DxilLibInitialize), while
                // DxcLoader.Register loads libdxcompiler, and DxilLibIsEnabled() never retries
                // after a miss, so a validated-DXIL compile opens nothing here.
                Result hr;
                using (DxcForkGate.Enter())
                {
                    hr = compile(
                        compiler.NativePointer,
                        &buffer,
                        (void*)argArray,
                        argCount,
                        (void*)includeHandlerPtr,
                        &iid,
                        &resultPtr);
                }

                GC.KeepAlive(includeHandler);
                hr.CheckError();

                return new IDxcResult(resultPtr);
            }
        }
        finally
        {
            foreach (nint p in argPointers)
            {
                if (p != 0)
                    Marshal.FreeHGlobal(p);
            }

            if (argArray != 0)
                Marshal.FreeHGlobal(argArray);
        }
    }

    /// <summary>
    /// Allocates a native NUL-terminated wide string in the platform's <c>wchar_t</c>
    /// encoding: UTF-16 on Windows (2-byte <c>wchar_t</c>), UTF-32 elsewhere (4-byte).
    /// </summary>
    private static nint AllocNativeWideString(string value)
    {
        if (OperatingSystem.IsWindows())
            return Marshal.StringToHGlobalUni(value);

        byte[] bytes = Encoding.UTF32.GetBytes(value);
        nint buffer = Marshal.AllocHGlobal(bytes.Length + sizeof(int));
        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        Marshal.WriteInt32(buffer, bytes.Length, 0); // UTF-32 NUL terminator
        return buffer;
    }
}
