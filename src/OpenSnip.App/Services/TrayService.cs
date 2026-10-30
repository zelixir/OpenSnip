using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using OpenSnip.Core;

namespace OpenSnip.App.Services;

/// <summary>托盘(H.NotifyIcon):菜单=截屏(F1)/贴图(F3)/退出;双击图标=截屏(SPEC §3.4)。须在 WPF UI 线程初始化。</summary>
public sealed class TrayService : IDisposable
{
    private TaskbarIcon? _icon;

    /// <summary>截屏请求(菜单项/双击图标)。</summary>
    public event Action? SnipRequested;

    /// <summary>贴图请求(菜单项)。</summary>
    public event Action? PasteRequested;

    /// <summary>退出请求(菜单项)。</summary>
    public event Action? ExitRequested;

    public void Initialize()
    {
        var menu = new ContextMenu();
        var snipItem = new MenuItem { Header = "截屏 (F1)" };
        snipItem.Click += (_, _) => SnipRequested?.Invoke();
        var pasteItem = new MenuItem { Header = "贴图 (F3)" };
        pasteItem.Click += (_, _) => PasteRequested?.Invoke();
        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        _ = menu.Items.Add(snipItem);
        _ = menu.Items.Add(pasteItem);
        _ = menu.Items.Add(new Separator());
        _ = menu.Items.Add(exitItem);

        var icon = new TaskbarIcon
        {
            ToolTipText = "OpenSnip — F1 截屏 / F3 贴图",
            IconSource = CreateIconSource(),
            ContextMenu = menu,
            Visibility = Visibility.Visible,
        };
        icon.TrayLeftMouseDoubleClick += (_, _) => SnipRequested?.Invoke();
        _icon = icon;
        TryForceCreate(icon); // 纯代码创建且无宿主窗口:显式创建,不依赖 Loaded 事件
        PerfLog.Log("App", "tray:ready");
    }

    private static ImageSource? CreateIconSource()
    {
        try
        {
            var frame = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
            TryFreeze(frame);
            return frame;
        }
        catch (Exception ex)
        {
            PerfLog.Log("App", $"tray:icon-load-failed {ex.Message}");
            return null;
        }
    }

    private static void TryFreeze(Freezable f)
    {
        try { if (!f.IsFrozen && f.CanFreeze) f.Freeze(); } catch { /* 静默 */ }
    }

    /// <summary>H.NotifyIcon 2.x:程序化创建、不进可视树的图标需显式 ForceCreate;旧版本无此 API 则反射跳过。</summary>
    private static void TryForceCreate(TaskbarIcon icon)
    {
        try
        {
            typeof(TaskbarIcon).GetMethod("ForceCreate", Type.EmptyTypes)?.Invoke(icon, null);
        }
        catch (Exception ex)
        {
            PerfLog.Log("App", $"tray:force-create-failed {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { _icon?.Dispose(); } catch { /* 已释放 */ }
        _icon = null;
    }
}
