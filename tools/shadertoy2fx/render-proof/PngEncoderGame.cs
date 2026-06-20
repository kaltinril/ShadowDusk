#nullable enable

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.ShaderToy.RenderProof;

/// <summary>
/// A throwaway headless MonoGame <see cref="Game"/> used solely to obtain a live
/// <see cref="GraphicsDevice"/> for PNG encoding (<see cref="Texture2D.SaveAsPng"/> requires one).
/// The encode callback runs once inside the device-ready context, then the game exits.
/// </summary>
public sealed class PngEncoderGame : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private Action<GraphicsDevice>? _work;

    public PngEncoderGame()
    {
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 16,
            PreferredBackBufferHeight = 16,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
    }

    /// <summary>Run <paramref name="work"/> once with a live graphics device, then exit.</summary>
    public void Encode(Action<GraphicsDevice> work)
    {
        _work = work;
        Run();
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_work is not null)
        {
            _work(GraphicsDevice);
            _work = null;
        }

        Exit();
    }
}
