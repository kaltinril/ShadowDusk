#nullable enable

using System.Runtime.InteropServices;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Xunit;

namespace ShadowDusk.ImageTests.GlContext;

/// <summary>
/// Signalled by <see cref="GlContextFixture.SkipIfNoContext"/> when no OpenGL
/// 3.3 context is available (no GPU, GLFW unloadable, headless CI without
/// software fallback). Tests that don't want to throw can instead check
/// <see cref="GlContextFixture.IsSkipped"/> and return early.
/// </summary>
public sealed class GlContextUnavailableException : Exception
{
    public GlContextUnavailableException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>
/// xUnit class fixture that establishes a single hidden GLFW window + OpenGL
/// 3.3 compatibility-profile context for the lifetime of the test class. All
/// draw commands target offscreen FBOs created via <see cref="CreateRenderer"/>.
///
/// <para>
/// We use the <b>Compatibility</b> profile rather than Core because the
/// cross-validation test suite needs to render both:
/// </para>
/// <list type="bullet">
///   <item>Modern GLSL 3.30+ (ShadowDusk emits via SPIRV-Cross — uses
///         <c>in</c>/<c>out</c>, named uniform blocks, <c>texture()</c>)</item>
///   <item>Legacy GLSL ES 1.0-style (mgfxc emits via MojoShader — uses
///         <c>varying</c>, <c>gl_FragColor</c>, <c>texture2D()</c>)</item>
/// </list>
/// <para>
/// Core 3.3 rejects GLSL ES <c>varying</c>; Compatibility 3.3 accepts both
/// dialects (the Compatibility profile keeps fixed-function and legacy GLSL
/// keywords available). The existing ShadowDusk-anchored regression suite
/// (12 tests) continues to render identically on Compatibility.
/// </para>
/// <para>
/// If context creation fails (e.g., no GPU, missing GLFW native, software
/// fallback disabled), <see cref="IsSkipped"/> is set and
/// <see cref="SkipReason"/> describes why. Tests should call
/// <see cref="SkipIfNoContext"/> at the top of every test body to short-circuit
/// with a clear diagnostic.
/// </para>
/// </summary>
public sealed class GlContextFixture : IAsyncLifetime
{
    private IWindow? _window;
    private GL?      _gl;

    public bool    IsSkipped  { get; private set; }
    public string? SkipReason { get; private set; }

    /// <summary>
    /// The shared GL API for this fixture. Only valid when
    /// <see cref="IsSkipped"/> is <c>false</c>.
    /// </summary>
    public GL Gl
    {
        get
        {
            SkipIfNoContext();
            return _gl!;
        }
    }

    /// <summary>
    /// The hidden 1x1 GLFW window backing the GL context. Only valid when
    /// <see cref="IsSkipped"/> is <c>false</c>.
    /// </summary>
    public IWindow Window
    {
        get
        {
            SkipIfNoContext();
            return _window!;
        }
    }

    public Task InitializeAsync()
    {
        // macOS: skip BEFORE touching GLFW. Two reasons:
        //   1. Native macOS OpenGL caps the Compatibility profile at 2.1, so the GL 3.3
        //      *Compatibility* context this suite requires (to link both modern GLSL 3.30+
        //      and legacy GLSL ES `varying`/`gl_FragColor` in one context) cannot be created
        //      — these tests would skip on macOS regardless.
        //   2. More importantly, initializing GLFW on macOS spins up Cocoa / NSApplication
        //      state on a non-background thread that prevents the test-host process from
        //      EXITING after the run completes (the "macOS `dotnet test` passes then the host
        //      hangs on exit until the job timeout" stall). TestSessionTimeout / --blame-hang
        //      never catch it because the tests themselves finish fine; it's process exit that
        //      hangs. Skipping before GLFW init avoids creating that thread entirely.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            IsSkipped  = true;
            SkipReason = "OpenGL 3.3 Compatibility is unavailable on macOS (native GL caps "
                       + "Compatibility at 2.1); the ImageTests render proxy is skipped on macOS.";
            return Task.CompletedTask;
        }

        try
        {
            // Ensure the GLFW backend is chosen even if other windowing
            // platforms (e.g., SDL) are present in the test environment.
            Silk.NET.Windowing.Window.PrioritizeGlfw();

            var options = WindowOptions.Default with
            {
                Size                       = new Vector2D<int>(1, 1),
                Title                      = "ShadowDusk.ImageTests (offscreen)",
                IsVisible                  = false,
                ShouldSwapAutomatically    = false,
                IsEventDriven              = true,
                // Compatibility profile (not Core) so both modern GLSL 3.30+
                // and legacy GLSL ES `varying`/`gl_FragColor` shaders link in
                // the same context. ForwardCompatible is a Core-only flag and
                // is intentionally omitted here.
                API                        = new GraphicsAPI(
                    ContextAPI.OpenGL,
                    ContextProfile.Compatability,
                    ContextFlags.Default,
                    new APIVersion(3, 3)),
                VSync                      = false,
                PreferredDepthBufferBits   = 16,
            };

            _window = Silk.NET.Windowing.Window.Create(options);
            _window.Initialize();
            _gl = GL.GetApi(_window);

            // _window.Initialize() leaves the context current on this thread.
            // xUnit may dispatch the test method body on a different thread,
            // and GLFW contexts are thread-local — so release it here and let
            // each test claim it via MakeContextCurrent().
            _window.GLContext?.Clear();
        }
        catch (Exception ex)
        {
            DisposeQuietly();
            IsSkipped  = true;
            SkipReason = $"OpenGL 3.3 context unavailable: {ex.GetType().Name}: {ex.Message}";
        }

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        DisposeQuietly();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Constructs a new <see cref="OffscreenRenderer"/> bound to this fixture's
    /// GL context. Throws <see cref="GlContextUnavailableException"/> if the
    /// fixture skipped initialization. Callers MUST hold the context current
    /// via <see cref="MakeContextCurrent"/> before invoking this method.
    /// </summary>
    public OffscreenRenderer CreateRenderer()
    {
        SkipIfNoContext();
        return new OffscreenRenderer(_gl!);
    }

    /// <summary>
    /// Makes this fixture's GL context current on the calling thread. xUnit
    /// may dispatch <see cref="IAsyncLifetime.InitializeAsync"/> and the test
    /// method body on different threads; GLFW contexts are thread-local, so
    /// every test that touches GL must call this first.
    ///
    /// <para>
    /// Returns a guard that releases the context when disposed. ALWAYS use it
    /// with <c>using</c> so the context isn't left bound to a thread that
    /// xUnit may not return to — otherwise the next theory row hits
    /// "WGL: The requested resource is in use." because the context is still
    /// considered held by a different thread.
    /// </para>
    /// </summary>
    public IDisposable MakeContextCurrent()
    {
        SkipIfNoContext();
        // Block other threads from concurrently grabbing the context. Each
        // theory row holds the lock for the duration of the test.
        System.Threading.Monitor.Enter(_contextLock);

        var ctx = _window!.GLContext;
        if (ctx is not null)
            ctx.MakeCurrent();
        return new ContextReleaseGuard(this);
    }

    private void ReleaseContext()
    {
        try
        {
            _window?.GLContext?.Clear();
        }
        finally
        {
            System.Threading.Monitor.Exit(_contextLock);
        }
    }

    private readonly object _contextLock = new();

    private sealed class ContextReleaseGuard : IDisposable
    {
        private readonly GlContextFixture _fixture;
        private bool _disposed;

        public ContextReleaseGuard(GlContextFixture fixture) => _fixture = fixture;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _fixture.ReleaseContext();
        }
    }

    /// <summary>
    /// Throws <see cref="GlContextUnavailableException"/> with the recorded
    /// skip reason when the fixture failed to create a GL context. Call this
    /// at the top of every test method that needs <see cref="Gl"/>.
    /// </summary>
    public void SkipIfNoContext()
    {
        if (IsSkipped)
            throw new GlContextUnavailableException(SkipReason ?? "GL context unavailable.");
    }

    private void DisposeQuietly()
    {
        try
        {
            _gl?.Dispose();
        }
        catch
        {
            // Disposal failures during fixture teardown shouldn't mask a more
            // useful test diagnostic.
        }
        finally
        {
            _gl = null;
        }

        try
        {
            _window?.Dispose();
        }
        catch
        {
            // ditto
        }
        finally
        {
            _window = null;
        }
    }
}
