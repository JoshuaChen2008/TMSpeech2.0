using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace TMSpeech.GUI.Converters;

/// <summary>把 int 与 ConverterParameter 比较：用于导航 RadioButton 选中态与设置页可见性。</summary>
public class IntEqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        return System.Convert.ToInt64(value) == long.Parse(parameter.ToString()!);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter != null) return int.Parse(parameter.ToString()!);
        return BindingOperations.DoNothing;
    }
}

/// <summary>uint 颜色值转 Brush：用于字幕实时预览。</summary>
public class UIntToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is uint u) return new SolidColorBrush(Color.FromUInt32(u));
        if (value is int i) return new SolidColorBrush(Color.FromUInt32(unchecked((uint)i)));
        if (value is long l) return new SolidColorBrush(Color.FromUInt32(unchecked((uint)l)));
        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

/// <summary>配置中的对齐枚举(int)转 Avalonia TextAlignment：用于字幕实时预览。</summary>
public class IntToTextAlignmentConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var v = value == null ? 0 : System.Convert.ToInt32(value);
        return v switch
        {
            1 => TextAlignment.Center,
            2 => TextAlignment.Right,
            3 => TextAlignment.Justify,
            _ => TextAlignment.Left,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
