#nullable enable

using System.Runtime.InteropServices;
using System.Text;
using ShadowDusk.Core.Preprocessor;
using SharpGen.Runtime;
using Vortice.Dxc;

namespace ShadowDusk.HLSL;

internal sealed class DxcIncludeHandler : CallbackBase, IDxcIncludeHandler
{
    private readonly IIncludeResolver _resolver;
    private readonly string _rootFilePath;
    private readonly IReadOnlyList<string> _additionalPaths;
    private readonly IDxcUtils _utils;

    // Owned native buffers that must outlive this handler (freed when handler is disposed).
    // DXC holds blob pointers referencing these buffers for the compilation lifetime,
    // so they cannot be GC-moved or freed until the compilation is complete.
    private readonly List<nint> _nativeBuffers = [];

    public DxcIncludeHandler(
        IDxcUtils utils,
        IIncludeResolver resolver,
        string rootFilePath,
        IReadOnlyList<string> additionalPaths)
    {
        _utils = utils;
        _resolver = resolver;
        _rootFilePath = rootFilePath;
        _additionalPaths = additionalPaths;
    }

    public Result LoadSource(string fileName, out IDxcBlob? includeSource)
    {
        var resolveResult = _resolver.Resolve(fileName, _rootFilePath, _additionalPaths);
        if (!resolveResult.IsSuccess)
        {
            includeSource = null;
            return Result.Fail;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(resolveResult.Value.Text);

        // Copy bytes to unmanaged memory so the buffer outlives this call frame
        // and remains stable while DXC holds a reference to the resulting blob.
        nint nativeBuffer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, nativeBuffer, bytes.Length);
        _nativeBuffers.Add(nativeBuffer);

        // DXC_CP_UTF8 = 65001 — the Win32 code page constant for UTF-8.
        _utils.CreateBlobFromPinned(nativeBuffer, bytes.Length, 65001, out IDxcBlobEncoding? blob);
        includeSource = blob;
        return Result.Ok;
    }

    public void FreeNativeBuffers()
    {
        foreach (nint buffer in _nativeBuffers)
            Marshal.FreeHGlobal(buffer);
        _nativeBuffers.Clear();
    }
}
