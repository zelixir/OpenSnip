using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace OpenSnip.App.Services;

/// <summary>
/// §12 命名规则:模板变量 $yyyy$/$MM$/$dd$/$HH$/$mm$/$ss$/$fff$/$title$/$exe$,
/// 非法字符过滤(逐路径段,目录分隔符保留);-o 路径同解析。
/// </summary>
public static partial class NamingRule
{
    /// <summary>默认模板(§12 默认名 Snip_日期_时间 的模板化表达;SPEC 原文尾部 $ss 缺闭合符,按 9 变量语法修正)。</summary>
    public const string DefaultTemplate = "Snip_$yyyy$-$MM$-$dd$_$HH$-$mm$-$ss$";

    /// <summary>历史存档固定文件名主体(§12:Snip_yyyyMMdd_HHmmss_fff.png)。</summary>
    public static string HistoryBaseName(DateTime now) => "Snip_" + now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);

    [GeneratedRegex("[<>:\"/\\\\|?*\\x00-\\x1F]")]
    private static partial Regex IllegalCharsRegex();

    /// <summary>渲染模板:时间变量 + $title$/$exe$(内容一并过滤);随后整路径逐段过滤非法字符。</summary>
    public static string Render(string? template, DateTime now, string title, string exe)
    {
        var t = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;
        var s = t
            .Replace("$yyyy$", now.ToString("yyyy", CultureInfo.InvariantCulture))
            .Replace("$MM$", now.ToString("MM", CultureInfo.InvariantCulture))
            .Replace("$dd$", now.ToString("dd", CultureInfo.InvariantCulture))
            .Replace("$HH$", now.ToString("HH", CultureInfo.InvariantCulture))
            .Replace("$mm$", now.ToString("mm", CultureInfo.InvariantCulture))
            .Replace("$ss$", now.ToString("ss", CultureInfo.InvariantCulture))
            .Replace("$fff$", now.ToString("fff", CultureInfo.InvariantCulture))
            .Replace("$title$", SanitizeSegment(title))
            .Replace("$exe$", SanitizeSegment(exe));
        return SanitizePath(s);
    }

    /// <summary>单段内容过滤:非法字符 → '_',结尾点/空格去除。</summary>
    public static string SanitizeSegment(string? s)
    {
        var clean = IllegalCharsRegex().Replace(s ?? "", "_").TrimEnd('.', ' ');
        return clean.Length == 0 ? "_" : clean;
    }

    /// <summary>整路径逐段过滤(目录分隔符 / 与 \ 保留,空段丢弃)。</summary>
    public static string SanitizePath(string s)
    {
        var parts = s.Split('/', '\\');
        var kept = new List<string>();
        foreach (var part in parts)
        {
            var clean = SanitizeSegment(part);
            if (clean.Length > 0 && clean != "_")
            {
                kept.Add(clean);
            }
        }
        return kept.Count == 0 ? SanitizeSegment(s) : Path.Combine(kept.ToArray());
    }
}
