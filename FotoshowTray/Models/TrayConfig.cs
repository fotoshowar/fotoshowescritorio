using System.Text.Json;

namespace FotoshowTray.Models;

/// <summary>
/// Configuración persistida en %APPDATA%\Fotoshow\config.json
/// </summary>
public class TrayConfig
{
    public string BackendUrl { get; set; } = "https://fotoshow.online";
    public string? JwtToken { get; set; }
    public string? PhotographerId { get; set; }
    public string? PhotographerName { get; set; }
    public bool AutoProcessEnabled { get; set; } = true;
    public int MaxConcurrentProcessing { get; set; } = 2;
    public int ThumbnailSize { get; set; } = 400;

    // ─── persistencia ──────────────────────────────────────────────────────────

    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fotoshow");

    private static readonly string ConfigPath =
        Path.Combine(ConfigDir, "config.json");

    public static TrayConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<TrayConfig>(json) ?? new TrayConfig();
            }
        }
        catch { /* primera ejecución o archivo corrupto */ }
        return new TrayConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }

    public bool IsLoggedIn => !string.IsNullOrEmpty(JwtToken);
}
