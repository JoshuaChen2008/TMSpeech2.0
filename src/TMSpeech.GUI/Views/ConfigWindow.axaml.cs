using System;
using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using ReactiveUI;
using TMSpeech.GUI.ViewModels;

namespace TMSpeech.GUI.Views
{
    public partial class ConfigWindow : ReactiveWindow<ConfigViewModel>
    {
        public ConfigWindow()
        {
            InitializeComponent();
            ViewModel = new ConfigViewModel();

            runVersion.Text = GitVersionInformation.FullSemVer;

            runInternalVersion.Text = GitVersionInformation.ShortSha +
                                      (GitVersionInformation.UncommittedChanges != "0" ? " (dirty)" : "");

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
