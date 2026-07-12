using TMSpeech.Core.Services.Notification;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using DesktopNotifications.FreeDesktop;
using DesktopNotifications.Windows;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using DesktopNotifications;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using TMSpeech.GUI.Desktop;
using Avalonia.Threading;
using TMSpeech.Core;

namespace TMSpeech.Services;

public class NotificationService : INotificationService
{
    public void Notify(string content, string? title, NotificationType type = NotificationType.Info)
    {
        var _notification = Program.NotificationManager;
        if (_notification == null || type >= NotificationType.Error)
        {
            // macos not supported
            Dispatcher.UIThread.Post(async () =>
            {
                await MessageBoxManager.GetMessageBoxStandard(title, content).ShowAsync();
            });
            return;
        }

        if (ConfigManagerFactory.Instance.Get<int>(NotificationConfigTypes.NotificationType) == NotificationConfigTypes.NotificationTypeEnum.None) return;
        var nf = new Notification
        {
            Title = title,
            Body = content
        };
        _ = ShowWhenReadyAsync(_notification, nf);
    }

    private static async Task ShowWhenReadyAsync(INotificationManager manager, Notification nf)
    {
        try
        {
            await AppBuilderExtensions.NotificationInitTask.ConfigureAwait(false);
            await manager.ShowNotification(nf).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"显示系统通知失败: {ex.Message}");
        }
    }
}

/// <summary>
/// Extensions for <see cref="AppBuilder" />
/// </summary>
public static class AppBuilderExtensions
{
    /// <summary>
    /// 通知管理器的异步初始化任务。初始化改为后台进行，避免阻塞应用启动；
    /// 发送通知前请先等待此任务完成。
    /// </summary>
    public static Task NotificationInitTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Setups the <see cref="INotificationManager" /> for the current platform and
    /// binds it to the service locator (<see cref="AvaloniaLocator" />).
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    public static AppBuilder SetupDesktopNotifications(this AppBuilder builder, out INotificationManager? manager)
    {
        try
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                var context = WindowsApplicationContext.FromCurrentProcess();
                manager = new WindowsNotificationManager(context);
            }
            else if (Environment.OSVersion.Platform == PlatformID.Unix)
            {
                var context = FreeDesktopApplicationContext.FromCurrentProcess();
                manager = new FreeDesktopNotificationManager(context);
            }
            else
            {
                //TODO: OSX once implemented/stable
                manager = null;
                return builder;
            }
        }
        catch (Exception ex)
        {
            // Notifications are optional. For example, Windows shortcut creation
            // can be denied by policy; the application must still be usable.
            Debug.WriteLine($"初始化系统通知失败，将禁用系统通知: {ex}");
            Trace.WriteLine($"初始化系统通知失败，将禁用系统通知: {ex}");
            manager = null;
            NotificationInitTask = Task.CompletedTask;
            return builder;
        }

        // 后台初始化，不阻塞启动（原先在此同步等待，拖慢窗口显示）
        NotificationInitTask = manager.Initialize();

        var manager_ = manager;
        builder.AfterSetup(b =>
        {
            if (b.Instance?.ApplicationLifetime is IControlledApplicationLifetime lifetime)
            {
                lifetime.Exit += (s, e) => { manager_.Dispose(); };
            }
        });

        return builder;
    }
}
