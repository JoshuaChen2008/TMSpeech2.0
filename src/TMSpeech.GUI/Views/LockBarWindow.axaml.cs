using Avalonia.Controls;
using Avalonia.Input;

namespace TMSpeech.GUI.Views;

/// <summary>
/// 锁定字幕后显示的悬浮控制条。主窗口锁定后整体鼠标穿透，无法再点击窗口内按钮，
/// 此小窗口保持可点击，提供解锁/开始/停止/重启/退出等操作（按钮可在设置中配置）。
/// 平时半透明，鼠标移入后完全显示。
/// </summary>
public partial class LockBarWindow : Window
{
    public LockBarWindow()
    {
        InitializeComponent();

        PointerEntered += (_, _) => Opacity = 1.0;
        PointerExited += (_, _) => Opacity = 0.35;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // 空白处按住可拖动控制条（不影响按钮点击）
        if (e.Source is Border or StackPanel)
        {
            BeginMoveDrag(e);
        }
    }
}
