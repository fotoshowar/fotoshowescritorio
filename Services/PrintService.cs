using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using System.IO;
using System.Linq;

namespace Fotoshow.Services;

/// <summary>
/// Servicio de impresión: composición A5 (300dpi) + marcos + ajustes
/// </summary>
public class PrintService
{
    // A5 @ 300dpi
    private const int A5_WIDTH_PORTRAIT = 1748;
    private const int A5_HEIGHT_PORTRAIT = 2480;
    private const int DEFAULT_MARGIN = 95; // ~8mm a 300dpi

    public class PrintOptions
    {
        public string PhotoPath { get; set; } = "";
        public string? FramePath { get; set; }
        public float Scale { get; set; } = 0.95f;           // 0.7 a 1.0
        public int MarginPixels { get; set; } = DEFAULT_MARGIN;
        public ImageAdjustments.AdjustmentParams? Adjustments { get; set; }
        public float OffsetX { get; set; } = 0;             // -1.0 a 1.0 (fracción del canvas)
        public float OffsetY { get; set; } = 0;             // -1.0 a 1.0
        public bool IsLandscape { get; set; } = false;
    }

    /// <summary>
    /// Compone una foto en formato A5 listo para imprimir
    /// </summary>
    public static Image<Rgba32> ComposeForPrint(PrintOptions options)
    {
        // Cargar foto y aplicar auto-rotate EXIF antes de todo
        using var photo = Image.Load<Rgba32>(options.PhotoPath);
        photo.Mutate(x => x.AutoOrient());   // respeta tag EXIF Orientation

        // Aplicar ajustes de imagen si hay
        var adjustedPhoto = options.Adjustments != null
            ? ImageAdjustments.ApplyAdjustments(photo, options.Adjustments)
            : photo.Clone();

        // Orientación basada en dimensiones REALES (ya rotadas por AutoOrient)
        var photoIsLandscape = adjustedPhoto.Width > adjustedPhoto.Height;
        var canvasWidth  = photoIsLandscape ? A5_HEIGHT_PORTRAIT : A5_WIDTH_PORTRAIT;
        var canvasHeight = photoIsLandscape ? A5_WIDTH_PORTRAIT  : A5_HEIGHT_PORTRAIT;

        // Calcular área disponible (restando márgenes)
        var availableWidth = (int)((canvasWidth - 2 * options.MarginPixels) * options.Scale);
        var availableHeight = (int)((canvasHeight - 2 * options.MarginPixels) * options.Scale);

        // Escalar foto manteniendo proporción
        var photoWidth = adjustedPhoto.Width;
        var photoHeight = adjustedPhoto.Height;
        var photoRatio = (float)photoWidth / photoHeight;
        var availableRatio = (float)availableWidth / availableHeight;

        int finalWidth, finalHeight;
        if (photoRatio > availableRatio)
        {
            // Foto más ancha: ajustar por ancho
            finalWidth = availableWidth;
            finalHeight = (int)(finalWidth / photoRatio);
        }
        else
        {
            // Foto más alta: ajustar por alto
            finalHeight = availableHeight;
            finalWidth = (int)(finalHeight * photoRatio);
        }

        adjustedPhoto.Mutate(x => x.Resize(finalWidth, finalHeight));

        // Crear canvas blanco A5
        var canvas = new Image<Rgba32>(canvasWidth, canvasHeight, Color.White);

        // Calcular posición centrada + offset
        var offsetXPixels = (int)(options.OffsetX * canvasWidth);
        var offsetYPixels = (int)(options.OffsetY * canvasHeight);
        
        var pasteX = (canvasWidth - finalWidth) / 2 + offsetXPixels;
        var pasteY = (canvasHeight - finalHeight) / 2 + offsetYPixels;

        // Pegar foto en canvas (con crop si sale del área)
        canvas.Mutate(ctx =>
        {
            // Calcular área de intersección
            var srcX = Math.Max(0, -pasteX);
            var srcY = Math.Max(0, -pasteY);
            var srcWidth = Math.Min(finalWidth - srcX, canvasWidth - Math.Max(0, pasteX));
            var srcHeight = Math.Min(finalHeight - srcY, canvasHeight - Math.Max(0, pasteY));

            var dstX = Math.Max(0, pasteX);
            var dstY = Math.Max(0, pasteY);

            if (srcWidth > 0 && srcHeight > 0)
            {
                var croppedPhoto = adjustedPhoto.Clone(x => 
                    x.Crop(new Rectangle(srcX, srcY, srcWidth, srcHeight)));
                
                ctx.DrawImage(croppedPhoto, new Point(dstX, dstY), 1.0f);
                croppedPhoto.Dispose();
            }
        });

        adjustedPhoto.Dispose();

        // Aplicar marco si existe
        if (!string.IsNullOrEmpty(options.FramePath) && File.Exists(options.FramePath))
        {
            using var frame = Image.Load<Rgba32>(options.FramePath);
            frame.Mutate(x => x.Resize(canvasWidth, canvasHeight));
            
            canvas.Mutate(ctx => ctx.DrawImage(frame, new Point(0, 0), 1.0f));
        }

        return canvas;
    }

    /// <summary>
    /// Guarda la imagen compuesta como JPEG optimizado para impresión
    /// </summary>
    public static string SaveForPrint(Image<Rgba32> image, string outputPath)
    {
        var encoder = new JpegEncoder
        {
            Quality = 95,
            ColorType = JpegEncodingColor.YCbCrRatio444
        };

        image.Save(outputPath, encoder);
        return outputPath;
    }

    /// <summary>
    /// Envía a imprimir usando el visor de fotos de Windows
    /// </summary>
    public static bool PrintWithWindows(string imagePath, string? printerName = null)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = imagePath,
                Verb = "print",
                UseShellExecute = true,
                CreateNoWindow = true
            };

            if (!string.IsNullOrEmpty(printerName))
            {
                startInfo.Arguments = $"\"{printerName}\"";
            }

            var process = System.Diagnostics.Process.Start(startInfo);
            process?.WaitForExit(10000); // Esperar máx 10 segundos
            
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Obtiene lista de impresoras disponibles en el sistema
    /// </summary>
    public static List<string> GetAvailablePrinters()
    {
        var printers = new List<string>();
        
        try
        {
            foreach (string printer in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
            {
                printers.Add(printer);
            }
        }
        catch
        {
            printers.Add("(Predeterminada)");
        }

        if (printers.Count == 0)
        {
            printers.Add("(Predeterminada)");
        }

        return printers;
    }

    /// <summary>
    /// Detecta orientación de una foto
    /// </summary>
    /// <summary>
    /// Detecta si la foto es landscape RESPETANDO el tag EXIF Orientation.
    /// Una foto tomada en vertical con el celular puede tener Width > Height
    /// en disco pero orientation = Rotate90, por eso hay que leer el EXIF.
    /// </summary>
    public static bool IsLandscape(string photoPath)
    {
        try
        {
            using var img = Image.Load<Rgba32>(photoPath);
            img.Mutate(x => x.AutoOrient());  // aplicar rotacion EXIF
            return img.Width > img.Height;
        }
        catch
        {
            // Fallback: comparar dimensiones crudas
            try
            {
                var info = Image.Identify(photoPath);
                return info != null && info.Width > info.Height;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Obtiene marcos disponibles para una orientación
    /// </summary>
    public static List<string> GetFramesForOrientation(bool isLandscape, string marcosDirectory)
    {
        if (!Directory.Exists(marcosDirectory))
            return new List<string>();

        var allFrames = Directory.GetFiles(marcosDirectory, "*.png");
        var filtered = new List<string>();

        foreach (var frame in allFrames)
        {
            var fileName = Path.GetFileName(frame).ToLower();
            
            if (isLandscape)
            {
                // Para landscape, excluir los que dicen "portrait" o "vertical"
                if (!fileName.Contains("portrait") && !fileName.Contains("vertical"))
                {
                    filtered.Add(frame);
                }
            }
            else
            {
                // Para portrait, excluir los que dicen "landscape" o "horizontal"
                if (!fileName.Contains("landscape") && !fileName.Contains("horizontal"))
                {
                    filtered.Add(frame);
                }
            }
        }

        return filtered;
    }
}
