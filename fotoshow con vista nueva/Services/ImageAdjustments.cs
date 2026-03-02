using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.PixelFormats;

namespace Fotoshow.Services;

/// <summary>
/// Ajustes de imagen: brillo, contraste, gamma, saturación, nitidez, temperatura
/// </summary>
public static class ImageAdjustments
{
    public class AdjustmentParams
    {
        public float Brightness { get; set; } = 0;      // -100 a 100
        public float Contrast { get; set; } = 0;        // -100 a 100
        public float Gamma { get; set; } = 1.0f;        // 0.5 a 2.0
        public float Saturation { get; set; } = 0;      // -100 a 100
        public float Sharpness { get; set; } = 0;       // 0 a 100
        public float Temperature { get; set; } = 0;     // -100 a 100 (frío a cálido)
    }

    /// <summary>
    /// Aplica todos los ajustes a una imagen
    /// Orden: Gamma → Brillo → Contraste → Temperatura → Saturación → Nitidez
    /// </summary>
    public static Image<Rgba32> ApplyAdjustments(Image<Rgba32> image, AdjustmentParams adjustments)
    {
        var result = image.Clone();

        // 1. Gamma (curva de potencia)
        if (Math.Abs(adjustments.Gamma - 1.0f) > 0.01f)
        {
            result.Mutate(x => x.Brightness(adjustments.Gamma));
        }

        // 2. Brillo
        if (Math.Abs(adjustments.Brightness) > 0.01f)
        {
            var brightnessFactor = 1.0f + (adjustments.Brightness / 100.0f);
            result.Mutate(x => x.Brightness(brightnessFactor));
        }

        // 3. Contraste
        if (Math.Abs(adjustments.Contrast) > 0.01f)
        {
            var contrastFactor = 1.0f + (adjustments.Contrast / 100.0f);
            result.Mutate(x => x.Contrast(contrastFactor));
        }

        // 4. Temperatura (ajuste de canales R/B)
        if (Math.Abs(adjustments.Temperature) > 0.01f)
        {
            result = ApplyTemperature(result, adjustments.Temperature);
        }

        // 5. Saturación
        if (Math.Abs(adjustments.Saturation) > 0.01f)
        {
            var saturationFactor = 1.0f + (adjustments.Saturation / 100.0f);
            result.Mutate(x => x.Saturate(saturationFactor));
        }

        // 6. Nitidez
        if (adjustments.Sharpness > 0.01f)
        {
            var sharpnessFactor = 1.0f + (adjustments.Sharpness / 40.0f);
            result.Mutate(x => x.GaussianSharpen(sharpnessFactor));
        }

        return result;
    }

    /// <summary>
    /// Aplica ajuste de temperatura (cálido/frío)
    /// Positivo = cálido (+rojo, -azul)
    /// Negativo = frío (-rojo, +azul)
    /// </summary>
    private static Image<Rgba32> ApplyTemperature(Image<Rgba32> image, float temperature)
    {
        var result = image.Clone();
        
        result.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                
                for (int x = 0; x < row.Length; x++)
                {
                    ref var pixel = ref row[x];
                    
                    if (temperature > 0) // Cálido
                    {
                        // Aumentar rojo, reducir azul
                        pixel.R = ClampByte(pixel.R + temperature * 0.8f);
                        pixel.B = ClampByte(pixel.B - temperature * 0.5f);
                    }
                    else // Frío
                    {
                        // Reducir rojo, aumentar azul
                        pixel.R = ClampByte(pixel.R + temperature * 0.5f);
                        pixel.B = ClampByte(pixel.B - temperature * 0.8f);
                    }
                }
            }
        });

        return result;
    }

    private static byte ClampByte(float value)
    {
        if (value < 0) return 0;
        if (value > 255) return 255;
        return (byte)value;
    }
}
