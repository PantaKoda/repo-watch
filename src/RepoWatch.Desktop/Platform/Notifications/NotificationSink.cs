namespace RepoWatch.Desktop.Platform.Notifications;

public enum NotificationAvailability
{
    Available,
    /// <summary>The user turned notifications off for Repo Watch (or for all apps) in the OS settings.</summary>
    DisabledByUser,
    /// <summary>An administrator's policy turned them off.</summary>
    DisabledByPolicy,
    /// <summary>This platform or build has no notification support in Repo Watch.</summary>
    Unsupported,
}

/// <summary>One notification. <see cref="Url"/> opens in the browser only when the user clicks it; nothing takes focus before.</summary>
public sealed record DesktopNotification(string Title, string Body, Uri? Url, string Tag);

/// <summary>Shows OS notifications. Implementations never throw for OS refusals; they report them.</summary>
public interface INotificationSink
{
    NotificationAvailability Availability { get; }

    /// <returns>False if the OS refused or notifications are unavailable.</returns>
    bool Show(DesktopNotification notification);
}

/// <summary>For builds and platforms without notification support: nothing is shown, and settings say so.</summary>
public sealed class UnsupportedNotificationSink : INotificationSink
{
    public NotificationAvailability Availability => NotificationAvailability.Unsupported;

    public bool Show(DesktopNotification notification) => false;
}
