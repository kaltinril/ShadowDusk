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
/// Thrown from <see cref="GlContextFixture.InitializeAsync"/> when the
/// cross-host GL lock could not be taken in time. Never a soft-skip: the host
/// HAS GL, another ImageTests host just would not let go of it.
/// </summary>
public sealed class GlHostSerializationException : Exception
{
    public GlHostSerializationException(string message)
        : base(message) { }
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
/// <para>
/// <b>Soft-skip hardening (Phase 37 tail item 4 / <c>SHADOWDUSK_REQUIRE_GL</c>):</b>
/// the <see cref="IsSkipped"/> early-return pattern means a headless host
/// reports every render test as PASS while rendering nothing — exactly the
/// Finding-B "paradox" that fabricated 27/27-green ubuntu runs while the
/// entire Linux compile path was broken. Two defenses live here now:
/// (1) when <c>SHADOWDUSK_REQUIRE_GL</c> is set (see <see cref="GlRequirement"/>),
/// a failed context creation THROWS from <see cref="InitializeAsync"/>, failing
/// every test in the class loudly — ci.yml's ubuntu lane sets it (running under
/// xvfb + Mesa llvmpipe) so a regression back to headless-skip turns the lane
/// red; (2) when unset, the soft-skip stays but emits an unmistakable
/// "GL SOFT-SKIP (rendered 0)" line per class so a log reader can tell
/// "passed" from "rendered". Note the macOS by-design skip below also honors
/// the gate: requiring GL on a macOS runner is a misconfiguration and fails
/// loudly rather than silently passing.
/// </para>
/// <para>
/// <b>Never hang the host (issue #345):</b> the window lives on a dedicated
/// owner thread (a window dies with the thread that created it), GL use is
/// serialized across concurrently running ImageTests hosts by a named mutex,
/// and every claim of the context goes through <see cref="GlContextGate"/>,
/// which fails a test on a native make-current error instead of leaving the
/// next test blocked, and ends the host if a GL call never returns.
/// </para>
/// </summary>
public sealed class GlContextFixture : IAsyncLifetime
{
    /// <summary>Name of the cross-process mutex that serializes GL use across ImageTests hosts.</summary>
    public const string HostLockName = "ShadowDusk.ImageTests.GlContext";

    /// <summary>How long a host waits for another ImageTests host to finish rendering.</summary>
    private static readonly TimeSpan HostLockTimeout = TimeSpan.FromMinutes(10);

    /// <summary>How long a test waits for another test in this host to release the context.</summary>
    private static readonly TimeSpan ContextAcquireTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long one test may hold the context. A render takes milliseconds; a
    /// lease held this long is a GL call that will never return.
    /// </summary>
    private static readonly TimeSpan ContextHoldLimit = TimeSpan.FromMinutes(3);

    private readonly ManualResetEventSlim _shutdown = new(false);
    private Thread?        _ownerThread;
    private IWindow?       _window;
    private GL?            _gl;
    private GlContextGate? _gate;

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

    /// <summary>
    /// <c>true</c> while the dedicated thread that created the window (and so
    /// keeps it alive on Windows) is running. Issue #345 regression check.
    /// </summary>
    public bool IsOwnerThreadAlive => _ownerThread?.IsAlive ?? false;

    public Task InitializeAsync()
    {
        // This GL render proxy is N/A on macOS BY PLATFORM DESIGN — it is a deliberate
        // decision, not a temporary workaround, and the coverage is not lost (the proxy
        // runs on Linux + Windows in CI; the real-runtime fidelity bar is the MonoGame
        // validation harness, not this proxy). Two independent platform facts make a
        // headless GL 3.3 Compatibility render impossible on a macOS CI runner:
        //   1. Apple DEPRECATED OpenGL (2018): native macOS GL caps the *Compatibility*
        //      profile at 2.1, but this suite needs GL 3.3 Compatibility to link BOTH modern
        //      GLSL 3.30+ (`in`/`out`) AND legacy GLSL ES (`varying`/`gl_FragColor`) in one
        //      context. That context simply cannot be created with Apple's GL.
        //   2. GLFW on macOS spins up Cocoa/NSApplication on a native NSThread that the .NET
        //      runtime cannot reap, so the test-host process never EXITS after a green run
        //      (confirmed unfixable upstream: glfw#1766; glfwTerminate does not release it).
        //      Skipping BEFORE GLFW init is what keeps macOS exiting cleanly.
        // (The only way to actually render GL on macOS headless is a from-source software
        // Mesa/OSMesa build — large, expensive on the 10x-metered macOS runner, and it would
        // only re-prove the Linux/Windows pixels. Not worth it for a proxy.)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            MarkSkippedOrThrow(
                "GL render proxy is N/A on macOS (Apple deprecated OpenGL; native GL "
                + "caps Compatibility at 2.1, and GLFW cannot exit cleanly on macOS). "
                + "This proxy is covered on Linux + Windows.");
            return Task.CompletedTask;
        }

        // Issue #345: the window and context are created (and later destroyed) on a
        // dedicated thread the fixture owns, never on whatever thread xUnit calls
        // InitializeAsync on. Windows destroys a window when the thread that created it
        // exits. That thread used to be a thread-pool worker; once the pool retired it
        // (20 s idle by default, reached under a loaded full-solution run) the next
        // wglMakeCurrent failed with "The handle is invalid".
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ownerThread = new Thread(() => OwnerThreadMain(ready))
        {
            IsBackground = true,
            Name         = "ShadowDusk.ImageTests GL owner",
        };
        _ownerThread.Start();
        return CompleteInitializationAsync(ready.Task);
    }

    private async Task CompleteInitializationAsync(Task ready)
    {
        try
        {
            await ready.ConfigureAwait(false);
        }
        catch (GlHostSerializationException)
        {
            // Not "no GL on this host": another ImageTests host held the GL lock
            // past the timeout. Fail every test loudly, never soft-skip.
            throw;
        }
        catch (Exception ex)
        {
            string reason = $"OpenGL 3.3 context unavailable: {ex.GetType().Name}: {ex.Message}";

            // Silk.NET reports a native-load failure as the one-line
            // "Couldn't find a suitable window platform" and SWALLOWS the real
            // loader exception (GlfwPlatform.IsApplicable catches it; the
            // detail only prints in Silk's own DEBUG builds). Constraint 5 —
            // fail loudly WITH diagnostics — so probe the GLFW native directly
            // and append the true dlopen/resolution error to the reason.
            if (ex is PlatformNotSupportedException)
                reason += ProbeGlfwLoadDetail();

            MarkSkippedOrThrow(reason);
            return;
        }

        _gate = new GlContextGate(
            makeCurrent:         () => _window!.GLContext?.MakeCurrent(),
            clearCurrent:        () => _window!.GLContext?.Clear(),
            acquireTimeout:      ContextAcquireTimeout,
            holdLimit:           ContextHoldLimit,
            onHoldLimitExceeded: OnContextHoldLimitExceeded);
    }

    /// <summary>
    /// Body of the fixture's GL owner thread: takes the cross-host GL lock,
    /// creates the hidden window + context, reports through <paramref name="ready"/>,
    /// then stays alive (and so keeps the window alive) until
    /// <see cref="DisposeAsync"/>, and destroys the window on this same thread,
    /// as Win32 requires.
    /// </summary>
    private void OwnerThreadMain(TaskCompletionSource ready)
    {
        Mutex? hostLock = null;
        try
        {
            hostLock = AcquireHostLock();
            CreateWindowAndContext();
        }
        catch (Exception ex)
        {
            DisposeWindowQuietly();
            ReleaseHostLock(hostLock);
            ready.TrySetException(ex);
            return;
        }

        ready.TrySetResult();
        _shutdown.Wait();
        DisposeWindowQuietly();
        ReleaseHostLock(hostLock);
    }

    /// <summary>
    /// Serializes GL use across ImageTests HOSTS (issue #345). A solution
    /// <c>dotnet test</c> runs the net8.0 and net10.0 hosts at the same time,
    /// each with its own window and context on the same desktop and GPU; this
    /// named mutex makes the second host's GL collection wait for the first
    /// one's to finish. Bounded: a host that cannot get the lock within
    /// <see cref="HostLockTimeout"/> fails its GL tests loudly instead of
    /// waiting forever. A mutex abandoned by a killed host is taken over.
    /// </summary>
    private static Mutex AcquireHostLock()
    {
        var mutex = new Mutex(initiallyOwned: false, HostLockName);
        try
        {
            if (mutex.WaitOne(TimeSpan.Zero))
                return mutex;

            Console.Error.WriteLine(
                "[ShadowDusk.ImageTests] Another ImageTests host is rendering; waiting for the "
                + $"cross-host GL lock '{HostLockName}' (issue #345).");
            if (mutex.WaitOne(HostLockTimeout))
                return mutex;
        }
        catch (AbandonedMutexException)
        {
            // Ownership passes to this thread; the previous holder died mid-run.
            Console.Error.WriteLine(
                $"[ShadowDusk.ImageTests] Took over the cross-host GL lock '{HostLockName}' "
                + "abandoned by a host that exited without releasing it.");
            return mutex;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }

        mutex.Dispose();
        throw new GlHostSerializationException(
            $"Timed out after {HostLockTimeout.TotalMinutes:0} min waiting for the cross-host GL lock "
            + $"'{HostLockName}': another ImageTests host held it the whole time. Failing instead of "
            + "waiting forever (issue #345).");
    }

    private static void ReleaseHostLock(Mutex? hostLock)
    {
        if (hostLock is null)
            return;

        try
        {
            hostLock.ReleaseMutex();
        }
        finally
        {
            hostLock.Dispose();
        }
    }

    private void CreateWindowAndContext()
    {
        // Preload the GLFW native by ABSOLUTE path (Linux). Phase 37 tail
        // finding, proven by the probe below on ubuntu-latest CI: the
        // deployed runtimes/linux-x64/native/libglfw.so.3 is a pristine
        // ELF and dlopen()s fine by absolute path, but Silk.NET's
        // name-based NativeLibrary resolution fails on the runner under
        // `dotnet test <slnx> --no-build` ("Could not load from any of
        // the possible library names!") — the testhost's native search
        // directories miss the test project's RID assets there (the same
        // bin output resolves fine locally and in a plain container).
        // Loading it once by absolute path makes glibc return the
        // already-loaded SONAME ("libglfw.so.3") for Silk's subsequent
        // dlopen-by-name — the same preload pattern as SpvcLoader /
        // DxcLoader / Vkd3dLoader in src/.
        PreloadGlfwNative();

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

        // _window.Initialize() leaves the context current on the owner thread.
        // GLFW contexts are thread-local and test bodies run on other threads,
        // so release it here and let each test claim it via MakeContextCurrent().
        _window.GLContext?.Clear();
    }

    /// <summary>
    /// Best-effort absolute-path preload of the deployed GLFW native so
    /// Silk.NET's dlopen-by-name resolves even where the testhost's native
    /// search directories don't cover the RID assets (observed on
    /// ubuntu-latest CI; see the call site comment). No-op off Linux and
    /// when the file is absent — failures fall through to the normal
    /// Silk.NET load + the probe diagnostics.
    /// </summary>
    private static void PreloadGlfwNative()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "linux-arm64"
            : "linux-x64";
        string path = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "libglfw.so.3");
        if (File.Exists(path))
            NativeLibrary.TryLoad(path, out _);
    }

    /// <summary>
    /// Diagnoses WHY Silk.NET deemed the GLFW platform "not applicable":
    /// re-runs the same native load Silk's <c>GlfwPlatform.IsApplicable</c>
    /// performs (capturing the swallowed exception), and on Linux also tries
    /// an absolute-path <see cref="NativeLibrary.Load(string)"/> of the
    /// deployed binary to separate "name/deps resolution failed" from
    /// "dlopen itself failed" (whose message carries the dlerror string).
    /// </summary>
    private static string ProbeGlfwLoadDetail()
    {
        string detail;
        try
        {
            using var glfw = Silk.NET.GLFW.Glfw.GetApi();
            detail = " [GLFW probe: Glfw.GetApi() succeeded — the windowing failure is elsewhere.]";
        }
        catch (Exception gex)
        {
            detail = $" [GLFW probe: Glfw.GetApi() failed: {gex.GetType().Name}: {gex.Message}]";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            string abs = Path.Combine(
                AppContext.BaseDirectory, "runtimes", "linux-x64", "native", "libglfw.so.3");
            if (!File.Exists(abs))
            {
                detail += $" [GLFW probe: '{abs}' does not exist.]";
            }
            else
            {
                try
                {
                    nint handle = NativeLibrary.Load(abs);
                    NativeLibrary.Free(handle);
                    detail += " [GLFW probe: absolute-path dlopen of the deployed libglfw.so.3 succeeded.]";
                }
                catch (Exception dex)
                {
                    // DllNotFoundException's message embeds the raw dlerror text.
                    detail += $" [GLFW probe: absolute-path dlopen FAILED: {dex.Message}]";
                }
            }
        }

        return detail;
    }

    /// <summary>
    /// Records the soft-skip (visibly) — or, when <c>SHADOWDUSK_REQUIRE_GL</c>
    /// is set, throws so every test in the class FAILS loudly instead of
    /// silently passing without rendering. See the class doc and
    /// <see cref="GlRequirement"/> for the Phase 37 Finding-B rationale.
    /// </summary>
    private void MarkSkippedOrThrow(string reason)
    {
        if (GlRequirement.IsRequired(Environment.GetEnvironmentVariable(GlRequirement.EnvVar)))
            throw new GlContextUnavailableException(GlRequirement.BuildFailureMessage(reason));

        IsSkipped  = true;
        SkipReason = reason;

        // Unmistakable per-class marker. ITestOutputHelper output only lands in
        // the TRX, so also write to the test-host's stderr — a log reader must
        // be able to see "rendered 0" next to a green summary.
        Console.Error.WriteLine(GlRequirement.BuildSoftSkipNotice(reason));
    }

    /// <summary>
    /// The standard line test bodies should write to <c>ITestOutputHelper</c>
    /// when early-returning because <see cref="IsSkipped"/> is set — keeps the
    /// "passed without rendering" marker consistent and greppable.
    /// </summary>
    public string SoftSkipLine =>
        $"SOFT-SKIP (rendered 0 — PASS without rendering): {SkipReason}";

    public Task DisposeAsync()
    {
        _shutdown.Set();

        // The owner thread destroys the window and releases the cross-host lock.
        // Bounded: a GLFW teardown stuck in the driver must not hold the host open
        // (the thread is a background thread, so it cannot block process exit).
        if (_ownerThread is not null && !_ownerThread.Join(TimeSpan.FromSeconds(30)))
        {
            Console.Error.WriteLine(
                "[ShadowDusk.ImageTests] The GL owner thread did not finish tearing down the window "
                + "within 30 s; leaving it to process exit.");
        }

        _gate?.Dispose();
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
    /// <para>
    /// A native make-current failure throws <see cref="GlContextLostException"/>
    /// (failing this test and, at once, every later one) and never leaves the
    /// context claimed, so the host keeps running to the end (issue #345).
    /// </para>
    /// </summary>
    public IDisposable MakeContextCurrent()
    {
        SkipIfNoContext();
        return _gate!.Acquire(DescribeWindowState);
    }

    private string DescribeWindowState()
    {
        string owner = $"GL owner thread alive: {IsOwnerThreadAlive}.";
        if (!OperatingSystem.IsWindows())
            return owner;

        nint hwnd = _window?.Native?.Win32?.Hwnd ?? 0;
        return $"{owner} Window handle 0x{hwnd:X} still valid: {IsWindow(hwnd)}.";
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    private static void OnContextHoldLimitExceeded(string message)
    {
        if (System.Diagnostics.Debugger.IsAttached)
            return;

        Console.Error.WriteLine(message);
        Environment.FailFast(message);
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

    private void DisposeWindowQuietly()
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
