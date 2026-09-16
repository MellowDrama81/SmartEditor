using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;

namespace SmartEditor.App.Android;

[Service(Exported = false, ForegroundServiceType = Android.Content.PM.ForegroundService.TypeDataSync)]
public sealed class GenerationForegroundService : Service
{
    private const string ChannelId = "generation";
    private const int NotificationId = 1001;
    private const string StartAction = "com.mellow.smarteditor.generation.START";
    private const string UpdateAction = "com.mellow.smarteditor.generation.UPDATE";
    private const string StopAction = "com.mellow.smarteditor.generation.STOP";
    private const string StatusExtra = "status";

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        switch (intent?.Action)
        {
            case StopAction:
                StopForeground(StopForegroundFlags.Remove);
                StopSelf();
                break;
            case StartAction:
                var notification = CreateNotification(intent.GetStringExtra(StatusExtra));
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                {
                    StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
                }
                else
                {
                    StartForeground(NotificationId, notification);
                }
                break;
            case UpdateAction:
                NotificationManagerCompat.From(this).Notify(NotificationId, CreateNotification(intent.GetStringExtra(StatusExtra)));
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
        if (foreground) context.StartForegroundService(intent); else context.StartService(intent);
    }

    private Notification CreateNotification(string? status)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(ChannelId, "Image generation", NotificationImportance.Low)
            {
                Description = "Keeps an active image generation running in the background.",
            };
            GetSystemService(NotificationService)!.CreateNotificationChannel(channel);
        }

        return new NotificationCompat.Builder(this, ChannelId)
            .SetSmallIcon(Resource.Drawable.smarteditor)
            .SetContentTitle("SmartEditor is generating an image")
            .SetContentText(status ?? "Generating image...")
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .Build();
    }
}
