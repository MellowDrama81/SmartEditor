using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;

namespace SmartEditor.App.Android;

[Service(Exported = false, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
public sealed class GenerationForegroundService : Service
{
    private const string ChannelId = "generation";
    private const int NotificationId = 1001;
    private const string StartAction = "com.mellow.smarteditor.generation.START";
    private const string UpdateAction = "com.mellow.smarteditor.generation.UPDATE";
    private const string StopAction = "com.mellow.smarteditor.generation.STOP";
    private const string StatusExtra = "status";

    public override IBinder? OnBind(Intent? intent) => null;

    /// <summary>
    /// Android 15+ grants only a bounded background runtime to <c>dataSync</c> foreground
    /// services. Stop inside the short grace period rather than letting the platform terminate the
    /// app with a foreground-service timeout exception. The Cloud job itself is durable and is
    /// reconciled through the recovery journal on the next launch.
    /// </summary>
    public override void OnTimeout(int startId, ForegroundService fgsType)
    {
        StopForegroundCompat();
        StopSelf(startId);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        switch (intent?.Action)
        {
            case StopAction:
                StopForegroundCompat();
                StopSelf();
                break;
            case StartAction:
                var notification = CreateNotification(intent.GetStringExtra(StatusExtra));
                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                {
                    StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
                }
                else
                {
                    StartForeground(NotificationId, notification);
                }
                break;
            case UpdateAction:
                NotificationManagerCompat.From(this)!.Notify(NotificationId, CreateNotification(intent!.GetStringExtra(StatusExtra)));
                break;
        }

        return StartCommandResult.NotSticky;
    }

    public static void Start(Context context, string status) => Send(context, StartAction, status, foreground: true);
    public static void Update(Context context, string status) => Send(context, UpdateAction, status, foreground: false);
    public static void Stop(Context context) => Send(context, StopAction, null, foreground: false);

    private static void Send(Context context, string action, string? status, bool foreground)
    {
        var intent = new Intent(context, typeof(GenerationForegroundService))!.SetAction(action);
        if (status is not null) intent.PutExtra(StatusExtra, status);
        if (foreground)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                context.StartForegroundService(intent);
            }
            else
            {
                context.StartService(intent);
            }
        }
        else
        {
            context.StartService(intent);
        }
    }

    private void StopForegroundCompat()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
        {
            StopForeground(StopForegroundFlags.Remove);
        }
        else
        {
#pragma warning disable CS0618 // deprecated bool overload is the only option below API 24
            StopForeground(true);
#pragma warning restore CS0618
        }
    }

    private Notification CreateNotification(string? status)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            var channel = new NotificationChannel(ChannelId, "Image generation", NotificationImportance.Low)
            {
                Description = "Keeps an active image generation running in the background.",
            };
            var notificationManager = GetSystemService(global::Android.Content.Context.NotificationService)
                as global::Android.App.NotificationManager;
            notificationManager?.CreateNotificationChannel(channel);
        }

        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetSmallIcon(Resource.Drawable.smarteditor);
        builder.SetContentTitle("SmartEditor is generating an image");
        builder.SetContentText(status ?? "Generating image...");
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        return builder.Build()!;
    }
}
