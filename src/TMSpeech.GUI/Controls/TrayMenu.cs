using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ReactiveUI;
using TMSpeech.Core;
using TMSpeech.GUI.Views;

namespace TMSpeech.GUI.Controls;

public class TrayMenu : NativeMenu
{
    private MainWindow _mainWindow;


    public void UpdateItems()
    {
        _mainWindow = (App.Current as App).MainWindow;
        this.Items.Clear();
        if (_mainWindow.ViewModel.IsLocked)
        {
            this.Items.Add(new NativeMenuItem
                { Header = "解锁字幕", Command = ReactiveCommand.Create(UnlockCaption) });
        }

        // 识别控制：无需解锁即可开始/停止/重启
        this.Items.Add(new NativeMenuItem { Header = "开始识别", Command = _mainWindow.ViewModel.PlayCommand });
        this.Items.Add(new NativeMenuItem { Header = "停止识别", Command = _mainWindow.ViewModel.StopCommand });
        this.Items.Add(new NativeMenuItem { Header = "重启识别", Command = _mainWindow.ViewModel.RestartCommand });
        this.Items.Add(new NativeMenuItemSeparator());

        this.Items.Add(new NativeMenuItem { Header = "重置窗口位置", Command = ReactiveCommand.Create(ResetWindowLocation) });
        this.Items.Add(new NativeMenuItem { Header = "退出", Command = ReactiveCommand.Create(Exit) });
    }

    private void ResetWindowLocation()
    {
        ConfigManagerFactory.Instance.DeleteAndApply<List<int>>(GeneralConfigTypes.MainWindowLocation);
        _mainWindow = (App.Current as App).MainWindow;
        _mainWindow.Position = new(100, 100);
    }

    private void Exit()
    {
        (App.Current as App).ExitApplication();
    }

    private void UnlockCaption()
    {
        _mainWindow.ViewModel.IsLocked = false;
    }
}