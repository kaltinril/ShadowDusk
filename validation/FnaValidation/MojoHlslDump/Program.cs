// Diagnostic scratch tool (Phase 39 Dissolve bisection): extracts every D3D9 SM2/SM3
// shader token stream from the given .fxb file(s) and runs each through the EXACT
// MojoShader the rung-3/4 harness runs (MOJOSHADER_parse, exported by the restored
// fnalibs FNA3D.dll) with profile "hlsl" — printing the HLSL source FNA3D's D3D11
// driver hands to D3DCompile at runtime. This shows precisely how MojoShader
// translated each candidate/reference instruction.
#nullable enable

using System.Buffers.Binary;
using System.Runtime.InteropServices;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: MojoHlslDump <file.fxb|file.bin> [...]");
    return 1;
}

foreach (string path in args)
{
    byte[] bytes = File.ReadAllBytes(path);
    Console.WriteLine($"=== {path} ({bytes.Length} bytes)");
    int found = 0;
    for (int i = 0; i + 8 <= bytes.Length; i += 4)
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4));
        if (v is 0xFFFF0300 or 0xFFFE0300 or 0xFFFF0200 or 0xFFFE0200)
        {
            int end = WalkStream(bytes, i);
            if (end < 0)
                continue;
            found++;
            Console.WriteLine($"--- shader #{found} @0x{i:X} ({end - i} bytes, version 0x{v:X8})");
            Translate(bytes.AsSpan(i, end - i).ToArray());
            i = end - 4; // loop's +=4 moves past
        }
    }
    if (found == 0)
        Console.WriteLine("(no D3D9 SM2/SM3 token streams found)");
}

return 0;

// Walks a D3D9 SM2/SM3 token stream from the version token; returns the byte offset
// just past the END token, or -1 if the walk derails (false-positive version token).
static int WalkStream(byte[] b, int start)
{
    int pos = start + 4;
    while (pos + 4 <= b.Length)
    {
        uint tok = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(pos, 4));
        if (tok == 0x0000FFFF)
            return pos + 4;
        if ((tok & 0xFFFF) == 0xFFFE && (tok & 0x8000_0000) == 0)
        {
            pos += 4 + (int)((tok >> 16) & 0x7FFF) * 4;
            continue;
        }
        if ((tok & 0x8000_0000) != 0)
            return -1; // a parameter token where an instruction should be — not a stream
        pos += 4 + (int)((tok >> 24) & 0xF) * 4;
    }
    return -1;
}

static unsafe void Translate(byte[] shader)
{
    IntPtr pd = MOJOSHADER_parse("hlsl", "main", shader, (uint)shader.Length,
                                 IntPtr.Zero, 0, IntPtr.Zero, 0,
                                 IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    try
    {
        // MOJOSHADER_parseData x64 layout: int error_count(0); MOJOSHADER_error* errors(8);
        // char* profile(16); char* output(24); int output_len(32)
        int errorCount = Marshal.ReadInt32(pd, 0);
        if (errorCount > 0)
        {
            IntPtr errors = Marshal.ReadIntPtr(pd, 8);
            for (int e = 0; e < errorCount; e++)
            {
                // MOJOSHADER_error x64 layout: char* error(0); char* filename(8); int pos(16); size 24
                IntPtr msg = Marshal.ReadIntPtr(errors, e * 24);
                int errPos = Marshal.ReadInt32(errors, e * 24 + 16);
                Console.WriteLine($"  MOJOSHADER ERROR @{errPos}: {Marshal.PtrToStringAnsi(msg)}");
            }
            return;
        }
        IntPtr output = Marshal.ReadIntPtr(pd, 24);
        int outputLen = Marshal.ReadInt32(pd, 32);
        Console.WriteLine(Marshal.PtrToStringAnsi(output, outputLen));
    }
    finally
    {
        MOJOSHADER_freeParseData(pd);
    }
}

[DllImport("FNA3D.dll", CallingConvention = CallingConvention.Cdecl)]
static extern IntPtr MOJOSHADER_parse(
    [MarshalAs(UnmanagedType.LPStr)] string profile,
    [MarshalAs(UnmanagedType.LPStr)] string mainfn,
    byte[] tokenbuf, uint bufsize,
    IntPtr swiz, uint swizcount,
    IntPtr smap, uint smapcount,
    IntPtr m, IntPtr f, IntPtr d);

[DllImport("FNA3D.dll", CallingConvention = CallingConvention.Cdecl)]
static extern void MOJOSHADER_freeParseData(IntPtr data);
