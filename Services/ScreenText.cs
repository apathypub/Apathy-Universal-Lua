using System.Windows;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Apathy.Services;

/// <summary>Reads text off the screen with Windows' built-in OCR (nothing to install beyond a Windows language).</summary>
public static class ScreenText
{
    private static OcrEngine? _engine;

    /// <summary>A recognized line and where it is on the screen (physical pixels).</summary>
    public sealed record Line(string Text, Int32Rect Box);

    /// <summary>Recognized lines in a screen rectangle (physical pixels); null when Windows has no OCR language.</summary>
    public static async Task<List<Line>?> ReadAsync(Int32Rect area)
    {
        _engine ??= OcrEngine.TryCreateFromUserProfileLanguages()
                    ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        if (_engine == null) return null;

        // Chat text is ~14 px high; the recognizer reads it far better enlarged, as long as it fits its size limit.
        int max = (int)OcrEngine.MaxImageDimension;
        int scale = Math.Max(area.Width, area.Height) * 2 <= max ? 2 : 1;
        int width = Math.Min(area.Width, max), height = Math.Min(area.Height, max);

        byte[]? pixels = Native.CaptureScreen(area.X, area.Y, width, height, scale);
        if (pixels == null) return [];
        MakeDarkOnLight(pixels);

        using var writer = new DataWriter();
        writer.WriteBytes(pixels);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8,
            width * scale, height * scale, BitmapAlphaMode.Premultiplied);
        var result = await _engine.RecognizeAsync(bitmap);
        return result.Lines.Select(line => new Line(line.Text, BoxOf(line, area, scale))).ToList();
    }

    // The union of the line's word boxes, mapped from the enlarged capture back to the screen.
    private static Int32Rect BoxOf(OcrLine line, Int32Rect area, int scale)
    {
        double left = line.Words.Min(w => w.BoundingRect.X), top = line.Words.Min(w => w.BoundingRect.Y);
        double right = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
        double bottom = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
        return new Int32Rect(area.X + (int)(left / scale), area.Y + (int)(top / scale),
                             (int)Math.Ceiling((right - left) / scale), (int)Math.Ceiling((bottom - top) / scale));
    }

    // CS2 draws light text on dark panels; the recognizer is most accurate on dark text over a light background.
    private static void MakeDarkOnLight(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte value = (byte)(255 - (bgra[i + 2] * 299 + bgra[i + 1] * 587 + bgra[i] * 114) / 1000);
            bgra[i] = bgra[i + 1] = bgra[i + 2] = value;
            bgra[i + 3] = 255;
        }
    }
}
