namespace OpenSnip.App.Services;

/// <summary>一次截屏的元数据(§12 历史记录用):截屏瞬间的前台窗口标题与进程名(v1,可能为空)。</summary>
public sealed record SnipMeta(string Title, string Exe)
{
    public static readonly SnipMeta Empty = new("", "");
}
