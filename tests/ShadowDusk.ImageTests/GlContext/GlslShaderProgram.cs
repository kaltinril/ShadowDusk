#nullable enable

using System.Text;
using Silk.NET.OpenGL;

namespace ShadowDusk.ImageTests.GlContext;

/// <summary>
/// Thrown when a GLSL shader fails to compile or link. The full GL info log is
/// embedded in <see cref="Exception.Message"/> so xUnit failure output shows
/// the underlying compiler diagnostic.
/// </summary>
public sealed class GlslCompileException : Exception
{
    public GlslCompileException(string message) : base(message) { }
}

/// <summary>
/// Owns a linked GL program built from a vertex/fragment GLSL pair. Vertex
/// attribute locations are pre-bound to match the layout produced by
/// SPIRV-Cross (POSITION=0, COLOR0=1, TEXCOORD0=2).
/// </summary>
public sealed class GlslShaderProgram : IDisposable
{
    public uint Handle { get; }

    private readonly GL _gl;
    private bool _disposed;

    private GlslShaderProgram(GL gl, uint handle)
    {
        _gl    = gl;
        Handle = handle;
    }

    public static GlslShaderProgram Compile(GL gl, string vertexSource, string fragmentSource)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(vertexSource);
        ArgumentNullException.ThrowIfNull(fragmentSource);

        uint vs = CompileStage(gl, ShaderType.VertexShader,   vertexSource,   "VS");
        uint fs;
        try
        {
            fs = CompileStage(gl, ShaderType.FragmentShader, fragmentSource, "FS");
        }
        catch
        {
            gl.DeleteShader(vs);
            throw;
        }

        uint program = gl.CreateProgram();
        gl.AttachShader(program, vs);
        gl.AttachShader(program, fs);

        // Bind every candidate attribute name to the canonical slot before
        // linking. Names that don't exist in the actual GLSL are ignored by the
        // driver, so over-binding is safe.
        //
        // - SPIRV-Cross emits `in_var_<SEMANTIC>` for modern GLSL output.
        // - MojoShader emits `vs_v0..vs_vN` for legacy GLSL ES output, where
        //   the index corresponds to the HLSL semantic order in the VS input
        //   struct (POSITION -> vs_v0, COLOR0 -> vs_v1, TEXCOORD0 -> vs_v2).
        // - Bare semantic names cover hand-written GLSL fixtures.
        BindAttribLocations(gl, program, location: 0, "in_var_POSITION", "in_var_SV_POSITION", "POSITION0", "POSITION", "vs_v0");
        BindAttribLocations(gl, program, location: 1, "in_var_COLOR0",   "in_var_COLOR",       "COLOR0",    "COLOR",    "vs_v1");
        BindAttribLocations(gl, program, location: 2, "in_var_TEXCOORD0","in_var_TEXCOORD",    "TEXCOORD0", "TEXCOORD", "vs_v2");

        gl.LinkProgram(program);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linkStatus);
        if (linkStatus == 0)
        {
            string log = gl.GetProgramInfoLog(program);
            gl.DetachShader(program, vs);
            gl.DetachShader(program, fs);
            gl.DeleteShader(vs);
            gl.DeleteShader(fs);
            gl.DeleteProgram(program);

            var sb = new StringBuilder();
            sb.AppendLine("GLSL program link failed.");
            sb.AppendLine("--- info log ---");
            sb.AppendLine(log);
            sb.AppendLine("--- vertex source ---");
            sb.AppendLine(vertexSource);
            sb.AppendLine("--- fragment source ---");
            sb.AppendLine(fragmentSource);
            throw new GlslCompileException(sb.ToString());
        }

        // Shader objects can be deleted as soon as they're linked into the
        // program; the program retains its own reference until glDeleteProgram.
        gl.DetachShader(program, vs);
        gl.DetachShader(program, fs);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);

        return new GlslShaderProgram(gl, program);
    }

    public void Use(GL gl)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ThrowIfDisposed();
        gl.UseProgram(Handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gl.DeleteProgram(Handle);
    }

    private static uint CompileStage(GL gl, ShaderType type, string source, string stageLabel)
    {
        uint shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);

        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compileStatus);
        if (compileStatus == 0)
        {
            string log = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);

            var sb = new StringBuilder();
            sb.AppendLine($"GLSL {stageLabel} compile failed.");
            sb.AppendLine("--- info log ---");
            sb.AppendLine(log);
            sb.AppendLine($"--- {stageLabel} source ---");
            sb.AppendLine(source);
            throw new GlslCompileException(sb.ToString());
        }

        return shader;
    }

    private static void BindAttribLocations(GL gl, uint program, uint location, params string[] names)
    {
        foreach (string name in names)
            gl.BindAttribLocation(program, location, name);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
