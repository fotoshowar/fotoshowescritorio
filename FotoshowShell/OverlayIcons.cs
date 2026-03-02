using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SharpShell.Attributes;
using SharpShell.SharpIconOverlayHandler;

// Registrar los tres handlers de overlay.
// Windows solo permite ~15 overlay handlers en total (los primeros por orden alfabético ganan).
// Prefijamos con espacios para subir en la lista (el mismo truco que usa OneDrive/Dropbox).

namespace FotoshowShell;

/// <summary>
/// Icono gris — foto en cola o procesándose
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid("A1B2C3D4-0001-0001-0001-000000000001")]
[COMServerAssociation(AssociationType.AllFiles)]
[DisplayName("  FotoshowPending")]
public class OverlayPending : SharpIconOverlayHandler
{
    protected override bool CanShowOverlay(string path, FILE_ATTRIBUTE attributes) =>
        StatusCache.GetStatus(path) == "pending" || StatusCache.GetStatus(path) == "processing";

    protected override System.Drawing.Icon GetOverlayIcon() =>
        IconLoader.Load("overlay_pending.ico");

    protected override int GetPriority() => 90; // menor número = mayor prioridad
}

/// <summary>
/// Icono azul — foto sincronizada al backend
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid("A1B2C3D4-0002-0002-0002-000000000002")]
[COMServerAssociation(AssociationType.AllFiles)]
[DisplayName("  FotoshowSynced")]
public class OverlaySynced : SharpIconOverlayHandler
{
    protected override bool CanShowOverlay(string path, FILE_ATTRIBUTE attributes) =>
        StatusCache.GetStatus(path) == "synced";

    protected override System.Drawing.Icon GetOverlayIcon() =>
        IconLoader.Load("overlay_synced.ico");

    protected override int GetPriority() => 91;
}

/// <summary>
/// Icono verde — foto vendida y entregada
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid("A1B2C3D4-0003-0003-0003-000000000003")]
[COMServerAssociation(AssociationType.AllFiles)]
[DisplayName("  FotoshowSold")]
public class OverlaySold : SharpIconOverlayHandler
{
    protected override bool CanShowOverlay(string path, FILE_ATTRIBUTE attributes) =>
        StatusCache.GetStatus(path) is "sold" or "delivered";

    protected override System.Drawing.Icon GetOverlayIcon() =>
        IconLoader.Load("overlay_sold.ico");

    protected override int GetPriority() => 92;
}

// ─── caché de estados — lee el SQLite compartido ────────────────────────────

/// <summary>
/// Lee el estado de las fotos desde el SQLite del tray (WAL mode, read-only).
/// Cachea en memoria por 5 segundos para no martillar el disco desde el Explorer.
/// </summary>
internal static class StatusCache
{
    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Fotoshow", "photos.db"
    );

    // cache: pathHash → (status, timestamp)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string status, DateTime ts)>
        _cache = new();

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    public static string GetStatus(string filePath)
    {
        try
        {
            if (!File.Exists(DbPath)) return "unknown";

            var hash = HashPath(filePath);

            // Verificar caché
            if (_cache.TryGetValue(hash, out var cached) &&
                DateTime.UtcNow - cached.ts < CacheTtl)
                return cached.status;

            // Leer de SQLite (solo SELECT — no bloquea gracias a WAL)
            var status = QueryStatus(hash);
            _cache[hash] = (status, DateTime.UtcNow);
            return status;
        }
        catch
        {
            return "unknown";
        }
    }

    private static string QueryStatus(string hash)
    {
        // SQLite directo sin EF (para evitar dependencias pesadas en la shell ext)
        var conn = $"Data Source={DbPath};Mode=ReadOnly;";
        using var connection = new System.Data.SQLite.SQLiteConnection(conn);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Status FROM Photos WHERE PathHash = @h LIMIT 1";
        cmd.Parameters.AddWithValue("@h", hash);
        var result = cmd.ExecuteScalar();
        return result?.ToString() ?? "unknown";
    }

    private static string HashPath(string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath).ToLowerInvariant();
        var bytes = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(normalized));
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}

// ─── cargador de iconos ─────────────────────────────────────────────────────

internal static class IconLoader
{
    private static readonly string IconDir = Path.Combine(
        Path.GetDirectoryName(typeof(IconLoader).Assembly.Location) ?? "",
        "icons"
    );

    public static System.Drawing.Icon Load(string name)
    {
        var path = Path.Combine(IconDir, name);
        if (File.Exists(path)) return new System.Drawing.Icon(path);
        return System.Drawing.SystemIcons.Information; // fallback
    }
}
