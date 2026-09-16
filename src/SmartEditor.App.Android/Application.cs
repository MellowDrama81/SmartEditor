using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Microsoft.Extensions.DependencyInjection;
using SmartEditor.App.Services;

namespace SmartEditor.App.Android
{
    [Application]
    public class Application : AvaloniaAndroidApplication<App>
    {
        protected Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
            App.ConfigurePlatformServices = services =>
                services.AddSingleton<IGenerationKeepAlive>(new AndroidGenerationKeepAlive(this));
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            return base.CustomizeAppBuilder(builder)
            .WithInterFont();
        }
    }
}
