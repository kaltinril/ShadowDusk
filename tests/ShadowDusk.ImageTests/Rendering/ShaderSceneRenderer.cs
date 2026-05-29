#nullable enable

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ShadowDusk.ImageTests.GlContext;
using Silk.NET.OpenGL;

namespace ShadowDusk.ImageTests.Rendering;

/// <summary>
/// Compiles a GLSL shader pair, uploads the standard interleaved unit quad,
/// binds uniforms / textures from a <see cref="SceneRender"/>, draws to the
/// supplied offscreen FBO, and reads the pixels back. One renderer is created
/// per <see cref="Render"/> call (cheap — VAO/VBO/EBO are tiny).
/// </summary>
public sealed class ShaderSceneRenderer
{
    // Standard quad: 4 vertices, 9 floats each.
    //   [0..2] float3 POSITION  (NDC)
    //   [3..6] float4 COLOR0    (magenta — so Minimal.fx renders magenta)
    //   [7..8] float2 TEXCOORD0 (0..1, top-left origin)
    //
    // Index order draws two triangles covering [-1,1]^2 in NDC.
    private static readonly float[] s_quadVertices =
    {
        // pos.xyz           color.rgba          uv.xy
        -1f, -1f, 0f,        1f, 0f, 1f, 1f,     0f, 1f, // bottom-left
         1f, -1f, 0f,        1f, 0f, 1f, 1f,     1f, 1f, // bottom-right
         1f,  1f, 0f,        1f, 0f, 1f, 1f,     1f, 0f, // top-right
        -1f,  1f, 0f,        1f, 0f, 1f, 1f,     0f, 0f, // top-left
    };

    private static readonly uint[] s_quadIndices =
    {
        0, 1, 2,
        0, 2, 3,
    };

    private readonly GL                 _gl;
    private readonly OffscreenRenderer  _fbo;

    public ShaderSceneRenderer(GL gl, OffscreenRenderer fbo)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(fbo);
        _gl  = gl;
        _fbo = fbo;
    }

    /// <summary>
    /// Optional sink for renderer diagnostics. Set this from a test before
    /// calling <see cref="Render"/> to capture per-attribute, per-uniform
    /// resolution traces. <c>null</c> = silent.
    /// </summary>
    public Action<string>? DiagnosticLogger { get; set; }

    /// <summary>
    /// Compiles the shader pair, draws the standard quad with the scene's
    /// clear color / uniforms / textures, and returns a row-flipped (top-left
    /// origin) 128*128*4 RGBA buffer.
    /// </summary>
    /// <param name="enableBlending">
    /// Set to <c>true</c> for shaders that declare AlphaBlendEnable in their
    /// pass state (e.g., <c>render-states.fx</c>). The renderer enables
    /// <c>GL_BLEND</c> with <c>SRC_ALPHA / ONE_MINUS_SRC_ALPHA</c> before
    /// drawing and disables it afterwards.
    /// </param>
    public byte[] Render(GlslShaderPair shaders, SceneRender scene, bool enableBlending = false)
    {
        ArgumentNullException.ThrowIfNull(shaders);
        ArgumentNullException.ThrowIfNull(scene);

        // Bind the offscreen FBO and clear it.
        _fbo.Clear(scene.ClearColor.R, scene.ClearColor.G, scene.ClearColor.B, scene.ClearColor.A);

        // Detect dialect: MojoShader-style PS uses `varying` / `gl_FragColor`,
        // while SPIRV-Cross-style PS uses `in vec` / out-variables. The
        // varying-name rewriting and the auto-injected VS only apply to the
        // modern dialect; MojoShader PS already declares its own varyings
        // matching the names the passthrough MojoShader VS emits.
        bool isMojoShaderDialect =
            shaders.FragmentSource.Contains("varying ",    StringComparison.Ordinal) ||
            shaders.FragmentSource.Contains("gl_FragColor", StringComparison.Ordinal);

        string vsSource = shaders.VertexSource
            ?? PassthroughVertexShader.PickFor(shaders.FragmentSource);

        // SPIRV-Cross emits `out_var_<SEMANTIC>` in the VS and `in_var_<SEMANTIC>`
        // in the PS for varyings, with no `layout(location=N)` qualifier. GLSL
        // links varyings by NAME, so VS-out vs. PS-in get treated as
        // disconnected variables — the PS reads zero, and the VS's input
        // attribute for that semantic is optimized away by the linker.
        //
        // Patch both stages so the varyings share the same name (`var_<SEM>`)
        // and survive linking. Vertex attribute inputs (the `in_var_<SEM>`
        // that appears only in VS) are left intact.
        //
        // The MojoShader dialect uses `vFrontColor` / `vTexCoord0` varying
        // names directly — no rewrite needed and no rewrite safe (these are
        // not `in_var_*` / `out_var_*` patterns).
        string rewrittenVs = isMojoShaderDialect ? vsSource : RewriteVertexShaderVaryings(vsSource);
        string rewrittenPs = isMojoShaderDialect ? shaders.FragmentSource : RewritePixelShaderVaryings(shaders.FragmentSource);

        using var program = GlslShaderProgram.Compile(_gl, rewrittenVs, rewrittenPs);
        program.Use(_gl);

        if (DiagnosticLogger is not null)
            LogProgramAttributes(_gl, program.Handle);

        // Setup VAO / VBO / EBO.
        uint vao = _gl.GenVertexArray();
        uint vbo = _gl.GenBuffer();
        uint ebo = _gl.GenBuffer();

        _gl.BindVertexArray(vao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        unsafe
        {
            fixed (float* p = s_quadVertices)
            {
                _gl.BufferData(
                    BufferTargetARB.ArrayBuffer,
                    (nuint)(s_quadVertices.Length * sizeof(float)),
                    p,
                    BufferUsageARB.StaticDraw);
            }
        }

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        unsafe
        {
            fixed (uint* p = s_quadIndices)
            {
                _gl.BufferData(
                    BufferTargetARB.ElementArrayBuffer,
                    (nuint)(s_quadIndices.Length * sizeof(uint)),
                    p,
                    BufferUsageARB.StaticDraw);
            }
        }

        const uint Stride = 9 * sizeof(float);

        // location 0 = POSITION (vec3)
        _gl.EnableVertexAttribArray(0);
        unsafe
        {
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, normalized: false, Stride, (void*)0);
        }

        // location 1 = COLOR0 (vec4)
        _gl.EnableVertexAttribArray(1);
        unsafe
        {
            _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, normalized: false, Stride, (void*)(3 * sizeof(float)));
        }

        // location 2 = TEXCOORD0 (vec2)
        _gl.EnableVertexAttribArray(2);
        unsafe
        {
            _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, normalized: false, Stride, (void*)(7 * sizeof(float)));
        }

        // Upload uniforms. We try plain glUniform* first; if a uniform was
        // wrapped in a UBO by SPIRV-Cross (which it almost always is when
        // sourced from HLSL cbuffers), the bare name will not resolve. In that
        // case, look up the UBO that contains the named member and upload to
        // a transient UBO bound to a binding point.
        var uboTracker = new UboBindingTracker(_gl, program.Handle);
        foreach (var kv in scene.Uniforms)
            UploadUniform(_gl, program.Handle, kv.Key, kv.Value, uboTracker, scene.MojoConstantRegisters);

        // Upload textures and bind to sequential texture units.
        var textureHandles = new List<uint>();
        try
        {
            int unit = 0;
            foreach (var kv in scene.Textures)
            {
                uint tex = UploadTexture(_gl, kv.Value);
                textureHandles.Add(tex);

                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
                _gl.BindTexture(TextureTarget.Texture2D, tex);

                // SPIRV-Cross emits combined image samplers as
                //   SPIRV_Cross_Combined<Texture><Sampler>
                BindSampler(_gl, program.Handle, kv.Key, unit);
                unit++;
            }

            if (enableBlending)
            {
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            }

            // Make sure depth test is disabled — our fullscreen quad doesn't
            // care, and render-states.fx explicitly disables it (z=0 quad).
            _gl.Disable(EnableCap.DepthTest);

            // Draw.
            unsafe
            {
                _gl.DrawElements(PrimitiveType.Triangles, (uint)s_quadIndices.Length, DrawElementsType.UnsignedInt, (void*)0);
            }
            _gl.Finish();

            if (enableBlending)
                _gl.Disable(EnableCap.Blend);
        }
        finally
        {
            foreach (uint tex in textureHandles)
                _gl.DeleteTexture(tex);
            uboTracker.Dispose();
            _gl.BindVertexArray(0);
            _gl.DeleteVertexArray(vao);
            _gl.DeleteBuffer(vbo);
            _gl.DeleteBuffer(ebo);
        }

        return _fbo.ReadPixels();
    }

    // Matches an entire word like `out_var_COLOR0`, `out_var_TEXCOORD0`, etc.
    private static readonly Regex s_outVarRegex = new(@"\bout_var_(\w+)\b", RegexOptions.Compiled);

    /// <summary>
    /// Rewrites every <c>out_var_&lt;SEM&gt;</c> in the vertex GLSL to
    /// <c>var_&lt;SEM&gt;</c> so its declared name matches the pixel shader's
    /// rewritten <c>var_&lt;SEM&gt;</c> input.
    /// </summary>
    private static string RewriteVertexShaderVaryings(string vs)
    {
        return s_outVarRegex.Replace(vs, m => "var_" + m.Groups[1].Value);
    }

    /// <summary>
    /// Rewrites every varying <c>in_var_&lt;SEM&gt;</c> in the pixel GLSL to
    /// <c>var_&lt;SEM&gt;</c>, with one important exception: vertex-attribute
    /// inputs in the VS share the same <c>in_var_&lt;SEM&gt;</c> spelling.
    /// In a PS file there are no vertex attributes (PS inputs are varyings),
    /// so a blanket rewrite is safe here.
    /// </summary>
    private static string RewritePixelShaderVaryings(string ps)
    {
        // All "in_var_<X>" in a PS source are varyings from the VS, so rewrite
        // unconditionally. We also rewrite "out_var_<X>" just in case (e.g.
        // out_var_SV_TARGET) to keep things consistent — that's a fragment
        // output, never matched against anything, so renaming it is harmless.
        string patched = Regex.Replace(ps, @"\bin_var_(\w+)\b", m =>
        {
            string sem = m.Groups[1].Value;
            // SV_TARGET is never an input to a PS, only an output. Leave any
            // "in_var_SV_TARGET" alone (none should exist).
            return "var_" + sem;
        });
        return patched;
    }

    private void LogProgramAttributes(GL gl, uint program)
    {
        gl.GetProgram(program, ProgramPropertyARB.ActiveAttributes, out int activeAttribs);
        DiagnosticLogger!($"Program {program}: {activeAttribs} active attribute(s)");
        for (uint i = 0; i < activeAttribs; i++)
        {
            string name = gl.GetActiveAttrib(program, i, out int size, out AttributeType type);
            int loc = gl.GetAttribLocation(program, name);
            DiagnosticLogger!($"  attrib[{i}] '{name}' size={size} type={type} location={loc}");
        }

        gl.GetProgram(program, ProgramPropertyARB.ActiveUniforms, out int activeUniforms);
        DiagnosticLogger!($"Program {program}: {activeUniforms} active uniform(s)");
        for (uint i = 0; i < activeUniforms; i++)
        {
            string name = gl.GetActiveUniform(program, i, out int size, out UniformType type);
            int loc = gl.GetUniformLocation(program, name);
            DiagnosticLogger!($"  uniform[{i}] '{name}' size={size} type={type} location={loc}");
        }

        gl.GetProgram(program, ProgramPropertyARB.ActiveUniformBlocks, out int activeBlocks);
        DiagnosticLogger!($"Program {program}: {activeBlocks} active uniform block(s)");
    }

    private static void UploadUniform(
        GL gl,
        uint program,
        string name,
        UniformValue value,
        UboBindingTracker ubo,
        IReadOnlyDictionary<string, int>? mojoConstantRegisters)
    {
        int loc = gl.GetUniformLocation(program, name);
        if (loc >= 0)
        {
            switch (value)
            {
                case UniformValue.FloatValue f:
                    gl.Uniform1(loc, f.V);
                    break;
                case UniformValue.Vec4Value v:
                    gl.Uniform4(loc, v.X, v.Y, v.Z, v.W);
                    break;
                case UniformValue.Mat4Value m:
                    // Column-major layout matching glUniformMatrix4fv(transpose=false).
                    gl.UniformMatrix4(loc, transpose: false, m.Values.AsSpan());
                    break;
            }
            return;
        }

        // MojoShader-dialect program (mgfxc golden): free uniforms live in an
        // unnamed `uniform vec4 ps_uniforms_vec4[N]` constant-register array, so
        // there is no uniform named e.g. `TintColor`. Bind by the supplied
        // constant-register index instead. Each register is a vec4; scalars are
        // broadcast so `.x` / `.xxxx` reads all see the value, and vec2/vec3/vec4
        // reads pick up the components they need.
        if (mojoConstantRegisters is not null &&
            mojoConstantRegisters.TryGetValue(name, out int register))
        {
            int arrLoc = gl.GetUniformLocation(program, $"ps_uniforms_vec4[{register}]");
            if (arrLoc >= 0)
            {
                (float x, float y, float z, float w) = value switch
                {
                    UniformValue.FloatValue f => (f.V, f.V, f.V, f.V),
                    UniformValue.Vec4Value v  => (v.X, v.Y, v.Z, v.W),
                    _                         => (0f, 0f, 0f, 0f),
                };
                gl.Uniform4(arrLoc, x, y, z, w);
                return;
            }
        }

        // Bare uniform name not found — try the UBO path (SPIRV-Cross wraps
        // HLSL cbuffers and global uniforms in std140 uniform blocks).
        ubo.SetMember(name, value);
    }

    private static void BindSampler(GL gl, uint program, string textureName, int unit)
    {
        // Order matters: SPIRV-Cross combined image samplers are named with
        // both the texture and the sampler in HLSL. We try the most likely
        // forms first.
        string[] candidates =
        {
            $"SPIRV_Cross_Combined{textureName}{textureName}Sampler",
            $"SPIRV_Cross_Combined{textureName}TextureSampler",
            $"SPIRV_Cross_Combined{textureName}Sampler",
            textureName,
            $"{textureName}Sampler",
        };

        foreach (string candidate in candidates)
        {
            int loc = gl.GetUniformLocation(program, candidate);
            if (loc >= 0)
            {
                gl.Uniform1(loc, unit);
                return;
            }
        }
        // Silently no-op if no candidate matches; the fragment shader will
        // sample whatever is bound at texture unit `unit` regardless.
    }

    private static uint UploadTexture(GL gl, TextureDescriptor desc)
    {
        uint handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, handle);

        ReadOnlySpan<byte> pixels = desc.RgbaPixels;
        unsafe
        {
            fixed (byte* p = pixels)
            {
                gl.TexImage2D(
                    target:         TextureTarget.Texture2D,
                    level:          0,
                    internalformat: InternalFormat.Rgba8,
                    width:          (uint)desc.Width,
                    height:         (uint)desc.Height,
                    border:         0,
                    format:         PixelFormat.Rgba,
                    type:           PixelType.UnsignedByte,
                    pixels:         p);
            }
        }
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,     (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,     (int)GLEnum.ClampToEdge);

        return handle;
    }

    /// <summary>
    /// Tracks one UBO per uniform block in the program so we can upload
    /// member values without having to know the block layout up front.
    /// Layout is std140 (matching the GLSL emitted by SPIRV-Cross for
    /// version 140 with FlipVertexY/FixupDepthConvention defaults).
    ///
    /// <para>
    /// Enumerates all active uniforms once on construction and builds a map
    /// from bare member name (the last segment after any '.') to its block
    /// index + offset. This avoids the brittleness of guessing whether
    /// glGetUniformIndices wants <c>"DiffuseColor"</c>,
    /// <c>"Transforms.DiffuseColor"</c>, or <c>"type_Transforms.DiffuseColor"</c>
    /// (drivers disagree).
    /// </para>
    /// </summary>
    private sealed class UboBindingTracker : IDisposable
    {
        private readonly GL                                 _gl;
        private readonly uint                               _program;
        private readonly Dictionary<uint, UboData>          _ubosByIndex = new();
        private readonly Dictionary<string, MemberLocation> _memberMap   = new(StringComparer.Ordinal);
        private uint                                        _nextBindingPoint;

        public UboBindingTracker(GL gl, uint program)
        {
            _gl      = gl;
            _program = program;
            BuildMemberMap();
        }

        public void SetMember(string memberName, UniformValue value)
        {
            if (!_memberMap.TryGetValue(memberName, out MemberLocation loc))
                return; // Uniform optimized away or genuinely absent.

            uint uboIndex = loc.BlockIndex;
            if (!_ubosByIndex.TryGetValue(uboIndex, out var ubo))
            {
                _gl.GetActiveUniformBlock(_program, uboIndex, UniformBlockPName.DataSize, out int blockSize);
                if (blockSize <= 0)
                    return;

                uint buffer = _gl.GenBuffer();
                _gl.BindBuffer(BufferTargetARB.UniformBuffer, buffer);
                unsafe
                {
                    _gl.BufferData(BufferTargetARB.UniformBuffer, (nuint)blockSize, (void*)0, BufferUsageARB.DynamicDraw);
                }

                uint bindingPoint = _nextBindingPoint++;
                _gl.BindBufferBase(BufferTargetARB.UniformBuffer, bindingPoint, buffer);
                _gl.UniformBlockBinding(_program, uboIndex, bindingPoint);

                ubo = new UboData(buffer, new byte[blockSize], bindingPoint);
                _ubosByIndex[uboIndex] = ubo;
            }

            WriteMember(ubo, loc.Offset, value);
        }

        private unsafe void BuildMemberMap()
        {
            _gl.GetProgram(_program, ProgramPropertyARB.ActiveUniforms, out int activeUniforms);
            for (uint i = 0; i < activeUniforms; i++)
            {
                string fullName = _gl.GetActiveUniform(_program, i, out _, out _);

                int blockIndex = -1;
                int offset     = -1;
                _gl.GetActiveUniforms(_program, 1u, &i, UniformPName.BlockIndex, &blockIndex);
                _gl.GetActiveUniforms(_program, 1u, &i, UniformPName.Offset,     &offset);

                if (blockIndex < 0)
                    continue; // Not part of a UBO.

                // Strip any prefix up to the last '.'. SPIRV-Cross uses both
                //   instance.member  (when instance name differs from block)
                //   type_block.member (active-uniform listing form on some drivers)
                int dot = fullName.LastIndexOf('.');
                string bareName = dot >= 0 ? fullName[(dot + 1)..] : fullName;

                // Strip any [N] array suffix on the bare member.
                int bracket = bareName.IndexOf('[');
                if (bracket >= 0)
                    bareName = bareName[..bracket];

                // Multiple uniforms might hash to the same bare name in theory
                // (two blocks with same member); first writer wins, which is
                // fine for our 9-fixture corpus.
                _memberMap.TryAdd(bareName, new MemberLocation((uint)blockIndex, offset));
            }
        }

        private readonly record struct MemberLocation(uint BlockIndex, int Offset);

        private void WriteMember(UboData ubo, int offset, UniformValue value)
        {
            // A member may be narrower than the value used to express it:
            // UniformValue has no vec2/vec3 case, so e.g. a vec2 'ScreenSize' is
            // supplied as a Vec4Value. In std140 such a member can sit at the tail
            // of its 16-byte block (vec2 after two floats), so writing a full 16
            // bytes would overrun the buffer. WriteFloat clamps each component to
            // the block's actual size; the shader only reads the components that
            // really exist (.xy for a vec2), so the dropped tail is never sampled.
            switch (value)
            {
                case UniformValue.FloatValue f:
                    WriteFloat(ubo.Cpu, offset, f.V);
                    break;
                case UniformValue.Vec4Value v:
                    WriteFloat(ubo.Cpu, offset,      v.X);
                    WriteFloat(ubo.Cpu, offset +  4, v.Y);
                    WriteFloat(ubo.Cpu, offset +  8, v.Z);
                    WriteFloat(ubo.Cpu, offset + 12, v.W);
                    break;
                case UniformValue.Mat4Value m:
                    for (int i = 0; i < m.Values.Length; i++)
                        WriteFloat(ubo.Cpu, offset + (i * sizeof(float)), m.Values[i]);
                    break;
            }

            // Re-upload entire CPU mirror to the UBO. Cheap — buffers are
            // at most a few hundred bytes.
            _gl.BindBuffer(BufferTargetARB.UniformBuffer, ubo.Handle);
            unsafe
            {
                fixed (byte* p = ubo.Cpu)
                {
                    _gl.BufferData(BufferTargetARB.UniformBuffer, (nuint)ubo.Cpu.Length, p, BufferUsageARB.DynamicDraw);
                }
            }
        }

        /// <summary>Writes a single float at <paramref name="offset"/>, skipping it if it
        /// would fall outside the block (a narrower-than-vec4 member at the block tail).</summary>
        private static void WriteFloat(byte[] buffer, int offset, float value)
        {
            if (offset >= 0 && offset + sizeof(float) <= buffer.Length)
                MemoryMarshal.Write(buffer.AsSpan(offset), in value);
        }

        public void Dispose()
        {
            foreach (var ubo in _ubosByIndex.Values)
                _gl.DeleteBuffer(ubo.Handle);
            _ubosByIndex.Clear();
        }

        private sealed record UboData(uint Handle, byte[] Cpu, uint BindingPoint);
    }
}
