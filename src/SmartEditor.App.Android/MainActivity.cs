using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

namespace SmartEditor.App.Android;

[Activity(
    Label = "SmartEditor",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/smarteditor",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
}
