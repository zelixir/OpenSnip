using OpenSnip.Core;

namespace OpenSnip.App.Services;

/// <summary>
/// §12 触发角集成适配层:按契约调用 Core.HotCornerMonitor(TimeSpan dwell)/Enabled set/CornerEntered event。
/// 说明:HotCornerMonitor 由 core-dev 并行交付(task-11);适配层用反射按契约签名解析,
/// Core 产物就绪即自动生效(避免 App 因上游未落地而整体编译失败);落地后可换直接引用(注释见下)。
/// 直接引用形态:
///   _monitor = new HotCornerMonitor(dwell); _monitor.Enabled = v; _monitor.CornerEntered += OnCorner;
/// </summary>
public sealed class HotCornerAdapter : IDisposable
{
    private object? _monitor;
    private System.Reflection.PropertyInfo? _enabledProp;
    private IDisposable? _disposable;

    /// <summary>Core 契约类型是否解析成功。</summary>
    public bool Available { get; private set; }

    /// <summary>不可用原因(诊断)。</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>角进入(0=左上 1=右上 2=左下 3=右下);由钩子线程派发。</summary>
    public event Action<int>? CornerEntered;

    public static HotCornerAdapter Create(TimeSpan dwell)
    {
        var adapter = new HotCornerAdapter();
        try
        {
            // typeof(PerfLog).Assembly = OpenSnip.Core 程序集(HistoryStore 是 App 本地类型,不能用它找 Core)
            var type = typeof(PerfLog).Assembly.GetType("OpenSnip.Core.HotCornerMonitor", throwOnError: false);
            if (type is null)
            {
                adapter.UnavailableReason = "OpenSnip.Core.HotCornerMonitor 未交付(task-11)";
                return adapter;
            }
            adapter._monitor = Activator.CreateInstance(type, dwell)
                ?? throw new InvalidOperationException("HotCornerMonitor 构造返回 null");
            adapter._enabledProp = type.GetProperty("Enabled")
                ?? throw new InvalidOperationException("HotCornerMonitor 缺 Enabled 属性");
            var evt = type.GetEvent("CornerEntered")
                ?? throw new InvalidOperationException("HotCornerMonitor 缺 CornerEntered 事件");
            var handlerType = evt.EventHandlerType ?? typeof(Action<int>);
            var handler = Delegate.CreateDelegate(handlerType, adapter, nameof(OnCornerRaw));
            evt.AddEventHandler(adapter._monitor, handler);
            adapter._disposable = adapter._monitor as IDisposable;
            adapter.Available = true;
        }
        catch (Exception ex)
        {
            adapter.Available = false;
            adapter.UnavailableReason = ex.Message;
        }
        return adapter;
    }

    /// <summary>运行时开关(契约:Enabled 只有 set)。</summary>
    public bool Enabled
    {
        set
        {
            if (_monitor is not null && _enabledProp is not null)
            {
                _enabledProp.SetValue(_monitor, value);
            }
        }
    }

    /// <summary>反射入口:事件回调按 Action<int> 签名转派。</summary>
    private void OnCornerRaw(int corner) => CornerEntered?.Invoke(corner);

    public void Dispose() => _disposable?.Dispose();
}
