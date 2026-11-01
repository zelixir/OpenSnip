using System.Globalization;
using System.IO;
using System.Text.Json;
using OpenSnip.Core;

namespace OpenSnip.App.Services;

/// <summary>
/// §12 历史存档:成功截图 → 配置目录\history\Snip_yyyyMMdd_HHmmss_fff.png + 同名 .json 元数据;
/// 上限 100 FIFO(超出删除最旧 png+json)。线程安全(单锁串行化,输出/交互线程都可能到达)。
/// </summary>
public sealed class HistoryStore
{
    /// <summary>历史条数上限(FIFO)。</summary>
    public const int Limit = 100;

    /// <summary>一条历史记录(List 用,新→旧排序)。</summary>
    public sealed record Entry(string File, string Time, int W, int H, string Title, string Exe);

    private readonly string _dir;
    private readonly object _gate = new();

    public HistoryStore(string directory) => _dir = directory;

    /// <summary>历史目录。</summary>
    public string Directory => _dir;

    /// <summary>保存一张成功截图;返回 (png 路径, json 路径)。失败抛异常由调用方兜底。</summary>
    public (string PngPath, string JsonPath) Save(NativeBitmap image, SnipMeta meta)
    {
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(_dir);
            var now = DateTime.Now;
            var baseName = NamingRule.HistoryBaseName(now);
            var pngPath = UniquePath(System.IO.Path.Combine(_dir, baseName + ".png"));
            var jsonPath = System.IO.Path.ChangeExtension(pngPath, ".json");

            var png = BitmapConversion.EncodePng(image);
            File.WriteAllBytes(pngPath, png);
            var metaObj = new Dictionary<string, object?>
            {
                ["file"] = System.IO.Path.GetFileName(pngPath),
                ["time"] = now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                ["w"] = image.Width,
                ["h"] = image.Height,
                ["title"] = meta.Title,
                ["exe"] = meta.Exe,
            };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(metaObj));
            EnforceLimit();
            return (pngPath, jsonPath);
        }
    }

    /// <summary>列出历史(新→旧,最多 count 条;count≤0 取全部)。</summary>
    public List<Entry> List(int count)
    {
        lock (_gate)
        {
            var result = new List<Entry>();
            if (!System.IO.Directory.Exists(_dir))
            {
                return result;
            }
            foreach (var png in System.IO.Directory.GetFiles(_dir, "*.png").OrderByDescending(f => f, StringComparer.Ordinal))
            {
                var entry = ReadEntry(png);
                result.Add(entry);
                if (count > 0 && result.Count >= count)
                {
                    break;
                }
            }
            return result;
        }
    }

    /// <summary>当前历史 png 总数。</summary>
    public int Total
    {
        get
        {
            lock (_gate)
            {
                return System.IO.Directory.Exists(_dir) ? System.IO.Directory.GetFiles(_dir, "*.png").Length : 0;
            }
        }
    }

    /// <summary>读单条元数据(json 缺失/损坏时从文件名与图像头兜底;再失败跳过)。</summary>
    private Entry ReadEntry(string pngPath)
    {
        var jsonPath = System.IO.Path.ChangeExtension(pngPath, ".json");
        var name = System.IO.Path.GetFileName(pngPath);
        try
        {
            if (File.Exists(jsonPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                var root = doc.RootElement;
                string Get(string k) => root.TryGetProperty(k, out var el) ? el.ToString() : "";
                int GetInt(string k) => root.TryGetProperty(k, out var el) && el.TryGetInt32(out var v) ? v : 0;
                return new Entry(Get("file") is { Length: > 0 } f ? f : name, Get("time"), GetInt("w"), GetInt("h"), Get("title"), Get("exe"));
            }
        }
        catch { /* 损坏 json 走兜底 */ }
        var stub = new Entry(name, System.IO.File.GetLastWriteTime(pngPath).ToString("yyyy-MM-dd HH:mm:ss"), 0, 0, "", "");
        return stub;
    }

    private void EnforceLimit()
    {
        var pngs = System.IO.Directory.GetFiles(_dir, "*.png").OrderByDescending(f => f, StringComparer.Ordinal).ToList();
        for (int i = Limit; i < pngs.Count; i++)
        {
            try
            {
                File.Delete(pngs[i]);
                var json = System.IO.Path.ChangeExtension(pngs[i], ".json");
                if (File.Exists(json))
                {
                    File.Delete(json);
                }
                PerfLog.Log("App", $"history:fifo-drop {System.IO.Path.GetFileName(pngs[i])}");
            }
            catch (Exception ex)
            {
                PerfLog.Log("App", $"history:fifo-drop-failed {ex.Message}");
            }
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var ext = System.IO.Path.GetExtension(path);
        for (int i = 1; i < 100; i++)
        {
            var candidate = System.IO.Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
        return path;
    }
}
