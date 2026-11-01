using System.Collections.Concurrent;
using System.Globalization;
using OpenSnip.Core;
using OpenSnip.Pin;

namespace OpenSnip.App.Services;

/// <summary>
/// App 侧贴图登记簿(§8.2 status.pins):
/// PinWindow 契约中 Topmost/OpacityPercent/ClickThrough 只有 setter、且不暴露 hwnd,
/// 故 App 登记自己施加的状态值,Closed 事件维护生命周期;hwnd 由"本进程可见顶层窗口矩形 == CurrentRect"反查。
/// </summary>
public sealed class PinRegistry
{
    private sealed record Meta(bool Topmost, byte Opacity, bool ClickThrough);

    private readonly ConcurrentDictionary<PinWindow, Meta> _pins = new();

    /// <summary>登记 App 创建的贴图并施加默认状态(置顶 / 100% 不透明 / 不穿透),随后 Closed 自动除名。</summary>
    public PinWindow Track(PinWindow pin, bool topmost = true, byte opacityPercent = 100, bool clickThrough = false)
    {
        ArgumentNullException.ThrowIfNull(pin);
        _pins[pin] = new Meta(topmost, opacityPercent, clickThrough);
        pin.Topmost = topmost;
        pin.OpacityPercent = opacityPercent;
        pin.ClickThrough = clickThrough;
        pin.Closed += OnClosed;
        return pin;
    }

    private void OnClosed(PinWindow pin)
    {
        _pins.TryRemove(pin, out _);
        try { pin.Closed -= OnClosed; } catch { /* 事件源已销毁 */ }
        PerfLog.Log("App", "pins:closed(登记簿移除)");
    }

    /// <summary>当前贴图数量。</summary>
    public int Count => _pins.Count;

    /// <summary>关闭全部贴图(§8.2 pin.closeAll);幂等,返回成功发起关闭的数量。</summary>
    public int CloseAll()
    {
        int n = 0;
        foreach (var kv in _pins)
        {
            try { kv.Key.Close(); n++; }
            catch (Exception ex) { PerfLog.Log("App", $"pins:close-failed {ex.Message}"); }
        }
        return n;
    }

    /// <summary>
    /// §12 触发角 show-images/hide-images 的 v1 实现:统一调整全部贴图不透明度
    /// (显示=100,隐藏=10;PinWindow 契约最低 10)。返回成功设置的数量。
    /// </summary>
    public int SetPinsOpacityAll(byte opacity)
    {
        int n = 0;
        foreach (var kv in _pins)
        {
            try { kv.Key.OpacityPercent = opacity; n++; }
            catch (Exception ex) { PerfLog.Log("App", $"pins:opacity-failed {ex.Message}"); }
        }
        return n;
    }

    /// <summary>status.pins 快照:hwnd 十进制字符串、rect=[x,y,w,h]、topmost/opacity/clickThrough(§8.2)。</summary>
    public List<Dictionary<string, object?>> Snapshot()
    {
        var result = new List<Dictionary<string, object?>>();
        if (_pins.IsEmpty)
        {
            return result;
        }
        var claimed = new HashSet<long>();
        var windows = AppWindowEnumerator.EnumerateOwnVisibleTopLevel();
        foreach (var kv in _pins)
        {
            var rect = kv.Key.CurrentRect; // 事实来源(真实实现内部加锁)
            var hwnd = string.Empty;
            foreach (var w in windows)
            {
                long id = w.Hwnd.ToInt64();
                if (w.ClassName == AppWindowEnumerator.OverlayClassName || claimed.Contains(id))
                {
                    continue;
                }
                if (w.Rect.Equals(rect))
                {
                    hwnd = id.ToString(CultureInfo.InvariantCulture);
                    claimed.Add(id);
                    break;
                }
            }
            result.Add(new Dictionary<string, object?>
            {
                ["hwnd"] = hwnd,
                ["rect"] = new[] { rect.X, rect.Y, rect.Width, rect.Height },
                ["topmost"] = kv.Value.Topmost,
                ["opacity"] = (int)kv.Value.Opacity,
                ["clickThrough"] = kv.Value.ClickThrough,
            });
        }
        return result;
    }
}
