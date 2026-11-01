using System.Globalization;

namespace OpenSnip.App.CommandLine;

/// <summary>顶层命令(SPEC §3.4:snip / paste / exit;§12:history;§15:whiteboard)。</summary>
public enum CliCommandKind { None, Snip, Paste, Exit, History, Whiteboard }

/// <summary>snip 的取图方式:交互遮罩 / 全屏直截 / 指定矩形直截。</summary>
public enum SnipMode { Interactive, Full, Area }

/// <summary>-o 输出目标种类(执行顺序恒为:FILE → clipboard → pin,§3.4)。</summary>
public enum OutputTargetKind { File, SaveDialog, Clipboard, Pin }

/// <summary>单个输出目标;File / SaveDialog 携带路径。</summary>
public sealed record OutputTarget(OutputTargetKind Kind, string Path = "");

/// <summary>解析结果。Ok=false 时 Error 为原因(此时仅 Block 供快速失败使用)。</summary>
public sealed record CliOptions(
    bool Ok,
    string? Error,
    bool Block,
    bool TestRpc,
    CliCommandKind Command,
    SnipMode Mode,
    int AreaX, int AreaY, int AreaW, int AreaH,
    double DelaySeconds,
    int HistoryCount,
    int WhiteboardColorArgb,
    bool ActiveScreen,
    IReadOnlyList<OutputTarget> Outputs,
    bool HasExplicitOutputs)
{
    public static CliOptions Fail(string error) => new(
        Ok: false, Error: error, Block: false, TestRpc: false, CliCommandKind.None, SnipMode.Interactive,
        0, 0, 0, 0, 0, 10, -1, false, Array.Empty<OutputTarget>(), HasExplicitOutputs: false);
}

/// <summary>
/// 命令行解析(SPEC §3.4 v1):
///   snip [--full | --area X Y W H] [--delay N] [-o clipboard|pin|&lt;FILE&gt;[;...]]
///   paste / exit;--block 为全局开关(任意位置)。
/// </summary>
public static class CommandLineParser
{
    public const string UsageText = """
        OpenSnip 命令行(SPEC §3.4):
          OpenSnip.App snip [--full | --area X Y W H] [--delay N] [-o clipboard|pin|<FILE>[;<FILE>...]]
          OpenSnip.App paste
          OpenSnip.App exit
        全局选项:
          --block        等待首实例执行完成后回写结果(超时 60s)
        说明:
          snip            交互式截屏(遮罩选区);不带 -o 时默认复制到剪贴板
          --full          直接截取主屏全屏
          --area X Y W H  直接截取虚拟桌面物理像素矩形(以中心点所在屏为准)
          --delay N       延迟 N 秒后执行(可与其余 snip 选项组合)
          -o              输出目标,分号分隔可组合;执行顺序:FILE → clipboard → pin
        """;

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        try
        {
            var command = CliCommandKind.None;
            var mode = SnipMode.Interactive;
            int ax = 0, ay = 0, aw = 0, ah = 0;
            double delay = 0;
            var outputs = new List<OutputTarget>();
            bool explicitOutputs = false;
            bool block = false;
            bool testRpc = false;
            int historyCount = 10; // §12:history [N] 默认 10
            int wbColor = -1;      // §15:whiteboard --color(#RRGGBB);-1=默认白
            bool activeScreen = false; // §15:whiteboard --active-screen

            for (int i = 0; i < args.Count; i++)
            {
                var tok = args[i];
                switch (tok.ToLowerInvariant())
                {
                    case "--block":
                        block = true; // 全局开关(SPEC §3.4 --block 语义)
                        break;
                    case "--test-rpc":
                        testRpc = true; // 全局开关(§8.2:测试模式,跳过单实例仲裁)
                        break;
                    case "--config":
                        // §8.2 预留("--test-rpc 可与 --config 并用"):v1 消费其值但不使用
                        if (i + 1 < args.Count) i += 1;
                        break;
                    case "snip":
                        if (command != CliCommandKind.None) return CliOptions.Fail($"命令重复:{tok}");
                        command = CliCommandKind.Snip;
                        break;
                    case "paste":
                        if (command != CliCommandKind.None) return CliOptions.Fail($"命令重复:{tok}");
                        command = CliCommandKind.Paste;
                        break;
                    case "exit":
                        if (command != CliCommandKind.None) return CliOptions.Fail($"命令重复:{tok}");
                        command = CliCommandKind.Exit;
                        break;
                    case "whiteboard": // §15:白板模式
                        if (command != CliCommandKind.None) return CliOptions.Fail($"命令重复:{tok}");
                        command = CliCommandKind.Whiteboard;
                        break;
                    case "--color": // §15:--color #RRGGBB(白板底色)
                    {
                        if (command != CliCommandKind.Whiteboard) return CliOptions.Fail("--color 只用于 whiteboard");
                        if (i + 1 >= args.Count) return CliOptions.Fail("--color 需要 #RRGGBB");
                        if (!TryParseColor(args[i + 1], out var argb)) return CliOptions.Fail($"--color 颜色格式非法(需 #RRGGBB):{args[i + 1]}");
                        wbColor = argb;
                        i += 1;
                        break;
                    }
                    case "--active-screen": // §15:仅当前活动屏
                        if (command != CliCommandKind.Whiteboard) return CliOptions.Fail("--active-screen 只用于 whiteboard");
                        activeScreen = true;
                        break;
                    case "history": // §12:history [N] — 列出最近 N 条历史(默认 10)
                        if (command != CliCommandKind.None) return CliOptions.Fail($"命令重复:{tok}");
                        command = CliCommandKind.History;
                        if (i + 1 < args.Count && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hc)
                            && hc > 0 && hc <= 100)
                        {
                            historyCount = hc;
                            i += 1;
                        }
                        break;
                    case "--full":
                    {
                        var err = RequireCommand(command, tok, CliCommandKind.Snip);
                        if (err is not null) return CliOptions.Fail(err);
                        if (mode != SnipMode.Interactive) return CliOptions.Fail("--full 与 --area 互斥");
                        mode = SnipMode.Full;
                        break;
                    }
                    case "--area":
                    {
                        var err = RequireCommand(command, tok, CliCommandKind.Snip);
                        if (err is not null) return CliOptions.Fail(err);
                        if (mode != SnipMode.Interactive) return CliOptions.Fail("--full 与 --area 互斥");
                        if (i + 4 >= args.Count) return CliOptions.Fail("--area 需要 4 个整数:X Y W H");
                        if (!TryParseInt(args[i + 1], out ax) || !TryParseInt(args[i + 2], out ay) ||
                            !TryParseInt(args[i + 3], out aw) || !TryParseInt(args[i + 4], out ah))
                            return CliOptions.Fail($"--area 参数必须是整数:{string.Join(' ', args.Skip(i + 1).Take(4))}");
                        if (aw <= 0 || ah <= 0) return CliOptions.Fail($"--area 宽高必须为正:W={aw} H={ah}");
                        mode = SnipMode.Area;
                        i += 4;
                        break;
                    }
                    case "--delay":
                    {
                        var err = RequireCommand(command, tok, CliCommandKind.Snip);
                        if (err is not null) return CliOptions.Fail(err);
                        if (i + 1 >= args.Count) return CliOptions.Fail("--delay 需要秒数 N");
                        if (!double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                            || v < 0 || v > 3600)
                            return CliOptions.Fail($"--delay 秒数非法(允许 0~3600):{args[i + 1]}");
                        delay = v;
                        i += 1;
                        break;
                    }
                    case "-o":
                    case "--output":
                    {
                        var err = RequireCommand(command, tok, CliCommandKind.Snip);
                        if (err is not null) return CliOptions.Fail(err);
                        if (i + 1 >= args.Count) return CliOptions.Fail("-o 需要参数:clipboard|pin|<FILE>(分号分隔)");
                        if (!TryParseOutputList(args[i + 1], outputs, out var oerr)) return CliOptions.Fail(oerr!);
                        explicitOutputs = true;
                        i += 1;
                        break;
                    }
                    default:
                        return CliOptions.Fail(tok.StartsWith('-') ? $"未知选项:{tok}" : $"无法识别的参数:{tok}");
                }
            }
            return new CliOptions(true, null, block, testRpc, command, mode, ax, ay, aw, ah, delay, historyCount, wbColor, activeScreen, outputs, explicitOutputs);
        }
        catch (Exception ex)
        {
            return CliOptions.Fail("参数解析异常:" + ex.Message);
        }
    }

    private static string? RequireCommand(CliCommandKind current, string token, CliCommandKind expected)
        => current == expected
            ? null
            : current == CliCommandKind.None
                ? $"选项 {token} 需要先给出命令 snip(例:OpenSnip.App snip --full)"
                : $"选项 {token} 只用于 snip 命令";

    /// <summary>解析 #RRGGBB / RRGGBB → 0xFFrrggbb(白板底色,alpha 恒 255)。</summary>
    private static bool TryParseColor(string s, out int argb)
    {
        argb = -1;
        var hex = s.StartsWith('#') ? s[1..] : s;
        if (hex.Length != 6) return false;
        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
        argb = unchecked((int)0xFF000000u) | rgb;
        return true;
    }

    private static bool TryParseInt(string s, out int v)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

    /// <summary>
    /// 解析 -o 列表:分号分隔;关键字 clipboard / pin 大小写不敏感;
    /// save 为契约外扩充(SaveFileDialog,默认名 Snip_yyyy-MM-dd_HH-mm-ss.png,§3.4 保存条目);
    /// 其余一律视为文件路径(展开环境变量)。
    /// </summary>
    private static bool TryParseOutputList(string value, List<OutputTarget> into, out string? error)
    {
        error = null;
        foreach (var seg in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (seg.ToLowerInvariant())
            {
                case "clipboard":
                    into.Add(new OutputTarget(OutputTargetKind.Clipboard));
                    break;
                case "pin":
                    into.Add(new OutputTarget(OutputTargetKind.Pin));
                    break;
                case "save":
                    into.Add(new OutputTarget(OutputTargetKind.SaveDialog));
                    break;
                default:
                    into.Add(new OutputTarget(OutputTargetKind.File, Environment.ExpandEnvironmentVariables(seg)));
                    break;
            }
        }
        if (into.Count == 0)
        {
            error = "-o 目标列表为空";
            return false;
        }
        return true;
    }
}
