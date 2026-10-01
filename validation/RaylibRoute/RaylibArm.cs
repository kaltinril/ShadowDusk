#nullable enable

using System.Runtime.InteropServices;
using Raylib_cs;

namespace ShadowDusk.Validation.RaylibRoute;

/// <summary>
/// The arm under test: the converter's fragment shader loaded into REAL raylib (Raylib-cs) with a
/// null vertex shader, exactly as a consumer would (<c>LoadShaderFromMemory(null, fs)</c>), and
/// drawn over the shared source with <c>BeginShaderMode</c> into a render texture.
/// </summary>
internal static unsafe class RaylibArm
{
    public static int Run(string outDir, JobFile jobs, List<string> failures)
    {
        int size = jobs.Size;
        Raylib.SetTraceLogLevel(TraceLogLevel.Warning);
        Raylib.SetConfigFlags(ConfigFlags.HiddenWindow);
        Raylib.InitWindow(size, size, "ShadowDusk raylib route: raylib arm (headless)");
        if (!Raylib.IsWindowReady())
            return Skip("raylib InitWindow did not produce a GL context");

        Console.WriteLine($"[raylib] raylib {Raylib.RAYLIB_VERSION}, default shader id {Rlgl.GetShaderIdDefault()}");

        byte[] sourceBytes = File.ReadAllBytes(JobIo.SourcePath(outDir));
        Texture2D source, extra;
        fixed (byte* data = sourceBytes)
        {
            var image = new Image
            {
                Data = data,
                Width = size,
                Height = size,
                Mipmaps = 1,
                Format = PixelFormat.UncompressedR8G8B8A8,
            };
            source = Raylib.LoadTextureFromImage(image);
            extra = Raylib.LoadTextureFromImage(image);
        }

        var tint = new Color(jobs.Tint[0], jobs.Tint[1], jobs.Tint[2], jobs.Tint[3]);
        var full = new Rectangle(0, 0, size, size);

        foreach (RenderJob job in jobs.Jobs)
        {
            if (job.FragmentPath is null)
                continue;

            // SpriteBatch draws the MonoGame arm with LinearClamp; raylib keeps filter and wrap on
            // the texture, so the same state is set here.
            foreach (Texture2D texture in new[] { source, extra })
            {
                Raylib.SetTextureFilter(texture, TextureFilter.Bilinear);
                Raylib.SetTextureWrap(texture, TextureWrap.Clamp);
            }

            Shader shader = LoadFragment(File.ReadAllText(job.FragmentPath));
            // raylib's failure mode is a silent fallback to its default shader, which would render
            // a plausible tinted copy of the source; it must be a hard failure here.
            if (shader.Id == Rlgl.GetShaderIdDefault() || !Raylib.IsShaderValid(shader))
            {
                failures.Add($"{job.Name}: raylib rejected the fragment shader (fell back to the default shader); see the raylib log above");
                continue;
            }

            foreach ((string name, float[] v) in job.Uniforms)
            {
                int loc = Raylib.GetShaderLocation(shader, name);
                if (loc < 0)
                    continue;
                ShaderUniformDataType type = v.Length switch
                {
                    1 => ShaderUniformDataType.Float,
                    2 => ShaderUniformDataType.Vec2,
                    3 => ShaderUniformDataType.Vec3,
                    4 => ShaderUniformDataType.Vec4,
                    _ => throw new InvalidDataException($"{job.Name}: uniform {name} has {v.Length} floats"),
                };
                fixed (float* p = v)
                    Raylib.SetShaderValue(shader, loc, p, type);
            }

            RenderTexture2D rt = Raylib.LoadRenderTexture(size, size);
            Raylib.BeginTextureMode(rt);
            Raylib.ClearBackground(new Color(0, 0, 0, 0));
            // MonoGame's BlendState.Opaque: the shader's output, alpha included, replaces the target.
            Rlgl.SetBlendFactors(Rlgl.ONE, Rlgl.ZERO, Rlgl.FUNC_ADD);
            Raylib.BeginBlendMode(BlendMode.Custom);
            Raylib.BeginShaderMode(shader);
            foreach (ExtraTexture texture in job.ExtraTextures)
                Raylib.SetShaderValueTexture(shader, Raylib.GetShaderLocation(shader, texture.RaylibUniform), extra);
            Raylib.DrawTexturePro(source, full, full, System.Numerics.Vector2.Zero, 0f, tint);
            Raylib.EndShaderMode();
            Raylib.EndBlendMode();
            Raylib.EndTextureMode();

            Image readback = Raylib.LoadImageFromTexture(rt.Texture);
            // GL stores the render texture bottom row first; raylib's own drawing convention is
            // top-down, so flip once to get rows top-first like the MonoGame arm's GetData.
            Raylib.ImageFlipVertical(&readback);
            var pixels = new byte[size * size * 4];
            Marshal.Copy((nint)readback.Data, pixels, 0, pixels.Length);
            File.WriteAllBytes(JobIo.ImagePath(outDir, "raylib", job.Name), pixels);
            Raylib.ExportImage(readback, Path.ChangeExtension(JobIo.ImagePath(outDir, "raylib", job.Name), ".png"));
            Raylib.UnloadImage(readback);
            Raylib.UnloadRenderTexture(rt);
            Raylib.UnloadShader(shader);
        }

        Raylib.UnloadTexture(source);
        Raylib.UnloadTexture(extra);
        Raylib.CloseWindow();
        return 0;
    }

    private static Shader LoadFragment(string fragment)
    {
        nint fs = Marshal.StringToCoTaskMemUTF8(fragment);
        try { return Raylib.LoadShaderFromMemory(null, (sbyte*)fs); }
        finally { Marshal.FreeCoTaskMem(fs); }
    }

    private static int Skip(string reason)
    {
        Console.Error.WriteLine($"[raylib] NO GL CONTEXT: {reason}");
        return 3;
    }
}
