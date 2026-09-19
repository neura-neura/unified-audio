namespace UnifiedAudio.Core.Audio;

public static class OverlayCircle
{
    // Top-down, premultiplied BGRA for UpdateLayeredWindow. No opaque matte.
    public static byte[] Render(int size, byte red, byte green, byte blue, byte alpha, int opacity)
    {
        if (size is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(size));
        var pixels = new byte[size * size * 4];
        var center = size / 2.0;
        var radius = size * 0.375;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var dx = x + 0.5 - center;
                var dy = y + 0.5 - center;
                var coverage = Math.Clamp(radius + 0.5 - Math.Sqrt(dx * dx + dy * dy), 0, 1);
                var a = (byte)Math.Round(alpha * Math.Clamp(opacity, 0, 100) / 100.0 * coverage);
                var offset = (y * size + x) * 4;
                pixels[offset] = (byte)((blue * a + 127) / 255);
                pixels[offset + 1] = (byte)((green * a + 127) / 255);
                pixels[offset + 2] = (byte)((red * a + 127) / 255);
                pixels[offset + 3] = a;
            }
        return pixels;
    }
}
