using System.Globalization;
using System.IO;
using System.Text;

namespace OpenSnip.App.Services;

/// <summary>App 固定路径(配置目录/历史/自动保存)。</summary>
internal static class AppPaths
{
    /// <summary>配置目录(%APPDATA%\OpenSnip)。</summary>
    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenSnip");

    /// <summary>config.ini 完整路径。</summary>
    public static string ConfigFile => Path.Combine(ConfigDirectory, "config.ini");

    /// <summary>历史存档目录(§12:固定 history\,FIFO 100)。</summary>
    public static string HistoryDirectory => Path.Combine(ConfigDirectory, "history");

    /// <summary>自动保存默认目录(配置 [Output] 目录 留空时)。</summary>
    public static string AutoSaveDirectory => Path.Combine(ConfigDirectory, "autosave");
}

/// <summary>
/// config.ini 模型(§12):[Output] auto-save/目录;[HotCorners] enabled/dwell_ms/actions。
/// 极小 INI 解析(section/key=value),首次运行写出带中文注释的默认模板。
/// </summary>
public sealed class AppConfig
{
    /// <summary>[Output] auto-save:成功截图自动保存(独立于 -o,§12)。</summary>
    public bool AutoSave;

    /// <summary>[Output] 目录:自动保存目录(留空 = 配置目录\autosave)。</summary>
    public string OutputDir = "";

    /// <summary>[HotCorners] enabled:触发角总开关。</summary>
    public bool HotCornersEnabled;

    /// <summary>[HotCorners] dwell_ms:停留判定毫秒(默认 300)。</summary>
    public int DwellMs = 300;

    /// <summary>[HotCorners] actions:四角动作(左上,右上,左下,右下);可选 show-images/hide-images/snip/paste/无。</summary>
    public string[] CornerActions = { "无", "无", "无", "无" };

    /// <summary>[SuperSnip] enabled:超级截屏(Win+左键)总开关,默认 false(§16)。</summary>
    public bool SuperSnipEnabled;

    /// <summary>加载 config.ini;不存在时写出默认模板。返回 (配置, 配置文件路径)。</summary>
    public static AppConfig Load()
    {
        Directory.CreateDirectory(AppPaths.ConfigDirectory);
        var cfg = new AppConfig();
        var path = AppPaths.ConfigFile;
        if (!File.Exists(path))
        {
            cfg.Save(path);
            return cfg;
        }
        string? section = "";
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (section.Equals("Output", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("auto-save", StringComparison.OrdinalIgnoreCase)) cfg.AutoSave = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (key == "目录") cfg.OutputDir = val;
            }
            else if (section.Equals("HotCorners", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("enabled", StringComparison.OrdinalIgnoreCase)) cfg.HotCornersEnabled = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (key.Equals("dwell_ms", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)) cfg.DwellMs = Math.Clamp(ms, 50, 5000);
                else if (key.Equals("actions", StringComparison.OrdinalIgnoreCase)) cfg.CornerActions = ParseActions(val);
            }
            else if (section.Equals("SuperSnip", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("enabled", StringComparison.OrdinalIgnoreCase)) cfg.SuperSnipEnabled = val.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }
        return cfg;
    }

    /// <summary>解析四角动作串(逗号分隔;无/空 = 不动作)。</summary>
    private static string[] ParseActions(string val)
    {
        var parts = val.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var result = new[] { "无", "无", "无", "无" };
        for (int i = 0; i < 4; i++)
        {
            if (i < parts.Length)
            {
                result[i] = parts[i] is "无" or "" ? "无" : parts[i];
            }
        }
        return result;
    }

    /// <summary>写出配置(带中文注释模板,保留当前值)。</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        sb.AppendLine("# OpenSnip 配置(修改后重启生效;触发角运行时开关走 TestRpc corners.set)");
        sb.AppendLine();
        sb.AppendLine("[Output]");
        sb.AppendLine("# 成功截图自动保存(独立于 -o 显式输出)");
        sb.AppendLine("auto-save=" + (AutoSave ? "true" : "false"));
        sb.AppendLine("# 自动保存目录(留空 = 配置目录\\autosave);文件名走命名规则模板($yyyy$ 等)");
        sb.AppendLine("目录=" + OutputDir);
        sb.AppendLine();
        sb.AppendLine("[HotCorners]");
        sb.AppendLine("# 触发角总开关(Core.HotCornerMonitor 就绪后生效)");
        sb.AppendLine("enabled=" + (HotCornersEnabled ? "true" : "false"));
        sb.AppendLine("# 鼠标停留判定毫秒(50~5000)");
        sb.AppendLine("dwell_ms=" + DwellMs.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("# 四角动作,逗号分隔,顺序:左上,右上,左下,右下;可选 show-images/hide-images/snip/paste/无");
        sb.AppendLine("actions=" + string.Join(",", CornerActions));
        sb.AppendLine();
        sb.AppendLine("[SuperSnip]");
        sb.AppendLine("# 超级截屏(Win 按住 + 左键;启用期间 Win 键被吞,开始菜单不可用)");
        sb.AppendLine("enabled=" + (SuperSnipEnabled ? "true" : "false"));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
