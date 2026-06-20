#nullable enable

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ShadowDusk.ShaderToy.Sample;

/// <summary>
/// The interactive capstone window. On startup, and whenever the user cycles shaders, it runs the
/// full RUNTIME path (ShaderToy GLSL -> <c>.fx</c> -> in-memory <c>.mgfx</c> via ShadowDusk -> live
/// <see cref="Effect"/>) and renders the result animated and interactive over a fullscreen quad.
///
/// <para>Controls: SPACE / RIGHT = next shader, LEFT = previous, mouse drives <c>iMouse</c>,
/// ESC = quit. The window title shows the current shader name and the uniforms it references.</para>
/// </summary>
public sealed class SampleGame : Game
{
    private const int Width = 960;
    private const int Height = 540;

    private readonly GraphicsDeviceManager _gdm;

    private int _index;
    private CompiledShaderToy? _current;
    private float _time;
    private int _frame;
    private Vector4 _mouse;

    private KeyboardState _prevKeyboard;

    public SampleGame()
    {
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = Width,
            PreferredBackBufferHeight = Height,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        IsMouseVisible = true;
        Window.AllowUserResizing = false;
    }

    protected override void LoadContent()
    {
        LoadCurrentShader();
    }

    private void LoadCurrentShader()
    {
        _current?.Effect?.Dispose();

        ShaderEntry entry = ShaderCatalog.Entries[_index];
        _current = SampleCompiler.Build(GraphicsDevice, entry);

        // Reset the animation clock so each freshly-compiled shader starts at t = 0.
        _time = 0f;
        _frame = 0;

        string uniforms = _current.UsedUniforms.Count > 0
            ? string.Join(", ", _current.UsedUniforms)
            : "(none)";
        string status = _current.Ok ? "compiled in-memory via ShadowDusk" : "ERROR (see console)";
        Window.Title =
            $"ShadowDusk ShaderToy sample  |  [{_index + 1}/{ShaderCatalog.Entries.Count}] " +
            $"{entry.DisplayName}  |  uniforms: {uniforms}  |  {status}  |  " +
            "SPACE/arrows cycle, ESC quit";

        // Surface a convert/compile failure once, on load, rather than crashing or spamming Draw.
        if (!_current.Ok)
            Console.Error.WriteLine($"[sample] {entry.DisplayName}:\n{_current.Error}");
    }

    protected override void Update(GameTime gameTime)
    {
        KeyboardState keyboard = Keyboard.GetState();

        if (keyboard.IsKeyDown(Keys.Escape))
        {
            Exit();
            return;
        }

        if (Pressed(keyboard, Keys.Space) || Pressed(keyboard, Keys.Right))
            Cycle(+1);
        else if (Pressed(keyboard, Keys.Left))
            Cycle(-1);

        _prevKeyboard = keyboard;

        // Drive the ShaderToy clock and mouse (ShaderToy's bottom-left-origin convention: y up,
        // and the click-position zw is only non-zero while a button is held).
        _time += (float)gameTime.ElapsedGameTime.TotalSeconds;
        _frame++;

        MouseState mouse = Mouse.GetState();
        float mx = mouse.X;
        float my = Height - 1 - mouse.Y; // flip to ShaderToy's bottom-left origin
        bool down = mouse.LeftButton == ButtonState.Pressed;
        _mouse = new Vector4(mx, my, down ? mx : 0f, down ? my : 0f);

        base.Update(gameTime);
    }

    private bool Pressed(KeyboardState now, Keys key) =>
        now.IsKeyDown(key) && _prevKeyboard.IsKeyUp(key);

    private void Cycle(int delta)
    {
        int count = ShaderCatalog.Entries.Count;
        _index = ((_index + delta) % count + count) % count;
        LoadCurrentShader();
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Color.Black);

        CompiledShaderToy? current = _current;
        if (current is { Ok: true, Effect: { } effect })
        {
            effect.SetResolution(Width, Height);
            effect.SetTime(_time);
            effect.SetTimeDelta((float)gameTime.ElapsedGameTime.TotalSeconds);
            effect.SetFrame(_frame);
            effect.SetMouse(_mouse);
            effect.Draw();
        }

        // On a convert/compile error the screen stays black: the title flags the error and the
        // diagnostic was already written to the console once (LoadCurrentShader) so the sample
        // never crashes on a bad shader.
        base.Draw(gameTime);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _current?.Effect?.Dispose();
        base.Dispose(disposing);
    }
}
