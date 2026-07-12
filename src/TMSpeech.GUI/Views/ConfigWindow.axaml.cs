using System;
using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using ReactiveUI;
using TMSpeech.Core;
using TMSpeech.GUI.ViewModels;

namespace TMSpeech.GUI.Views
{
    public partial class ConfigWindow : ReactiveWindow<ConfigViewModel>
    {
        public ConfigWindow()
        {
            InitializeComponent();
            ViewModel = new ConfigViewModel();

            runVersion.Text = BuildVersionInfo.Version;
            runInternalVersion.Text = BuildVersionInfo.InternalVersion;

            UpdateThemeButton();

            // 主题变体切换（点按钮或系统深浅色变化）后，当前可见设置页的已合成内容
            // 不会自动重绘，需要强制重建一次，否则停留在旧主题的配色上。
            ActualThemeVariantChanged += (_, _) => RefreshCurrentPane();
        }

        private void RefreshCurrentPane()
        {
            if (ViewModel == null) return;
            var tab = ViewModel.CurrentTab;
            ViewModel.CurrentTab = -1;
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => ViewModel.CurrentTab = tab,
                Avalonia.Threading.DispatcherPriority.Render);
        }

        /// <summary>顶栏主题按钮：循环切换 跟随系统 → 浅色 → 深色，立即生效并持久化。</summary>
        private void ThemeButton_OnClick(object? sender, RoutedEventArgs e)
        {
            var next = ThemeManager.Next(ViewModel?.GeneralSectionConfig.Theme);
            if (ViewModel != null) ViewModel.GeneralSectionConfig.Theme = next;
            ThemeManager.Apply(next);
            UpdateThemeButton();
        }

        private void UpdateThemeButton()
        {
            var theme = ViewModel?.GeneralSectionConfig.Theme ?? GeneralConfigTypes.ThemeEnum.System;
            btnTheme.Content = theme switch
            {
                GeneralConfigTypes.ThemeEnum.Light => "", // 太阳
                GeneralConfigTypes.ThemeEnum.Dark => "", // 月亮
                _ => "", // 显示器（跟随系统）
            };
            ToolTip.SetTip(btnTheme, theme switch
            {
                GeneralConfigTypes.ThemeEnum.Light => "主题：浅色（点击切换）",
                GeneralConfigTypes.ThemeEnum.Dark => "主题：深色（点击切换）",
                _ => "主题：跟随系统（点击切换）",
            });
        }

        private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        }

        private void TitleBar_OnDoubleTapped(object? sender, TappedEventArgs e)
        {
            ToggleMaximized();
        }

        private void MinimizeButton_OnClick(object? sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_OnClick(object? sender, RoutedEventArgs e)
        {
            ToggleMaximized();
        }

        private void CloseButton_OnClick(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ToggleMaximized()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
    }
}
