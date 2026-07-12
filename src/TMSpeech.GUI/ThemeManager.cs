using System;
using Avalonia;
using Avalonia.Styling;
using TMSpeech.Core;

namespace TMSpeech.GUI;

/// <summary>界面主题应用：system 跟随系统 / light 浅色 / dark 深色。</summary>
public static class ThemeManager
{
    /// <summary>从配置读取主题并应用（启动时调用）。</summary>
    public static void ApplyFromConfig()
    {
        string? theme = null;
        try
        {
            theme = ConfigManagerFactory.Instance.Get<string>(GeneralConfigTypes.Theme);
        }
        catch
        {
            // 读取失败按跟随系统处理
        }

        Apply(theme);
    }

    public static void Apply(string? theme)
    {
        var app = Application.Current;
        if (app == null) return;

        app.RequestedThemeVariant = theme switch
        {
            GeneralConfigTypes.ThemeEnum.Light => ThemeVariant.Light,
            GeneralConfigTypes.ThemeEnum.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    /// <summary>循环切换：跟随系统 → 浅色 → 深色 → 跟随系统。</summary>
    public static string Next(string? theme) => theme switch
    {
        GeneralConfigTypes.ThemeEnum.System => GeneralConfigTypes.ThemeEnum.Light,
        GeneralConfigTypes.ThemeEnum.Light => GeneralConfigTypes.ThemeEnum.Dark,
        _ => GeneralConfigTypes.ThemeEnum.System,
    };
}
