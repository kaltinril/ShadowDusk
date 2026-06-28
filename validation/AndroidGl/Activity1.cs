using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Microsoft.Xna.Framework;

namespace ShadowDusk.Validation.AndroidGl;

/// <summary>
/// Phase 50 on-device-compile harness entry point. Launches <see cref="FiddleGame"/>, which
/// compiles an HLSL string to a .mgfx IN MEMORY, AT RUNTIME, on the device via ShadowDusk and
/// loads it into a live MonoGame Effect.
/// </summary>
[Activity(
    Label = "ShadowDusk AndroidGl",
    MainLauncher = true,
    AlwaysRetainTaskState = true,
    LaunchMode = LaunchMode.SingleInstance,
    ScreenOrientation = ScreenOrientation.FullSensor,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.ScreenSize)]
public class Activity1 : AndroidGameActivity
{
    protected override void OnCreate(Bundle? bundle)
    {
        base.OnCreate(bundle);
        var game = new FiddleGame();
        SetContentView((View)game.Services.GetService(typeof(View)));
        game.Run();
    }
}
