#nullable enable

using System.Runtime.InteropServices;
using Xunit;

namespace ShadowDusk.HLSL.Tests.D3DCompiler;

/// <summary>
/// An xUnit <see cref="FactAttribute"/> that is skipped off Windows. The DXBC
/// oracle backend P/Invokes d3dcompiler_47.dll, which only exists on Windows;
/// off-Windows behavior is covered separately by <c>ReturnsClearError</c> tests.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Skip = "DXBC oracle backend requires Windows (d3dcompiler_47.dll).";
    }
}
