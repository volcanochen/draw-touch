using System;
using System.IO;
using System.Text.Json;

namespace SpenTabletHost;

/// <summary>appsettings.json 的读写。始终放在 EXE 所在目录，保证双击启动时也能找到。</summary>
internal static class ConfigStore
{
    public static string Path
    {
        get
        {
            string dir;
            try { dir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory(); }
            catch { dir = Directory.GetCurrentDirectory(); }
            if (string.IsNullOrEmpty(dir)) dir = Directory.GetCurrentDirectory();
            return System.IO.Path.Combine(dir, "appsettings.json");
        }
    }

    public static MappingConfig Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var json = File.ReadAllText(Path);
                var cfg = JsonSerializer.Deserialize<MappingConfig>(json);
                if (cfg != null) return cfg;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"读取 {Path} 失败，使用默认配置：{ex.Message}");
        }
        return new MappingConfig();
    }

    public static void Save(MappingConfig cfg)
    {
        try
        {
            File.WriteAllText(Path, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
            AppLog.Info($"[配置] 已保存到 {Path}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"保存配置失败：{ex.Message}");
        }
    }
}
