using OpenSnip.Core;

namespace OpenSnip.App.Services;

/// <summary>
/// §16 超级截屏触发器适配层:按契约调用 Core.SuperSnipTrigger(参数less 构造/Enabled set/SnipRequested event)。
/// 反射解析与 HotCornerAdapter 同理 —— core-dev(task-11/16)产物就绪即自动生效,
/// 避免上游未落地时 App 整体编译失败;落地后可换直接引用(见 HotCornerAdapter 注释)。
/// config.ini [SuperSnip] enabled(默认 false)。
/// </summary>
public sealed class SuperSnipAdapter : IDisposable
{
    private object? _trigger;
    private System.Reflection.PropertyInfo? _enabledProp;
    private IDisposable? _disposable;

    /// <summary>Core 契约类型是否解析成功。</summary>
    public bool Available { get; private set; }

    /// <summary>不可用原因(诊断)。</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>超级截屏请求(Win 按住 + 左键按下;钩子线程派发)。</summary>
    public event Action? SnipRequested;

    public static SuperSnipAdapter Create()
    {
        var adapter = new SuperSnipAdapter();
        try
        {
            var type = typeof(PerfLog).Assembly.GetType("OpenSnip.Core.SuperSnipTrigger", throwOnError: false);
            if (type is null)
            {
                adapter.UnavailableReason = "OpenSnip.Core.SuperSnipTrigger 未交付(core-dev)";
                return adapter;
            }
            adapter._trigger = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("SuperSnipTrigger 构造返回 null");
            adapter._enabledProp = type.GetProperty("Enabled")
                ?? throw new InvalidOperationException("SuperSnipTrigger 缺 Enabled 属性");
            var evt = type.GetEvent("SnipRequested")
                ?? throw new InvalidOperationException("SuperSnipTrigger 缺 SnipRequested 事件");
            var handler = Delegate.CreateDelegate(evt.EventHandlerType ?? typeof(Action), adapter, nameof(OnSnipRaw));
            evt.AddEventHandler(adapter._trigger, handler);
            adapter._disposable = adapter._trigger as IDisposable;
            adapter.Available = true;
        }
        catch (Exception ex)
        {
            adapter.Available = false;
            adapter.UnavailableReason = ex.Message;
        }
        return adapter;
    }

    /// <summary>运行时开关(契约:Enabled 只有 set;LL 键盘钩子,Win 键被吞)。</summary>
    public bool Enabled
    {
        set
        {
            if (_trigger is not null && _enabledProp is not null)
            {
                _enabledProp.SetValue(_trigger, value);
            }
        }
    }

    /// <summary>反射入口:事件回调按 Action 签名转派。</summary>
    private void OnSnipRaw() => SnipRequested?.Invoke();

    public void Dispose() => _disposable?.Dispose();
}
