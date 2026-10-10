namespace Fonoo.Windows.Desktop;

// Small vector badges rasterized once per state/size. No UI animation or new dependency.
public static class StatusBadgePixels
{
    public static byte[] Draw(TrayStatusKind kind, int size, byte[]? background = null)
    {
        if (size is < 16 or > 64 || (background is not null && background.Length != size * size * 4)) throw new ArgumentException("Invalid icon size");
        var pixels = background?.ToArray() ?? new byte[size * size * 4];
        var overlay = background is null;
        var center = overlay ? size / 2d : size * .765;
        var radius = overlay ? size * .46 : size * .225;
        var color = kind switch {
            TrayStatusKind.Ready => (R: 29, G: 154, B: 74),
            TrayStatusKind.DoNotDisturb => (R: 196, G: 43, B: 28),
            TrayStatusKind.Held or TrayStatusKind.Muted => (R: 173, G: 107, B: 22),
            TrayStatusKind.Ringing or TrayStatusKind.Calling or TrayStatusKind.Call => (R: 110, G: 74, B: 217),
            _ => (R: 117, G: 113, B: 130)
        };
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
        {
            double red = 0, green = 0, blue = 0, alpha = 0;
            for (var sy = 0; sy < 4; sy++) for (var sx = 0; sx < 4; sx++)
            {
                var nx = (x + (sx + .5) / 4 - center) / radius;
                var ny = (y + (sy + .5) / 4 - center) / radius;
                var distance = Math.Sqrt(nx * nx + ny * ny);
                if (distance > 1) continue;
                var white = distance > .87 || Ink(kind, nx, ny);
                red += white ? 255 : color.R; green += white ? 255 : color.G; blue += white ? 255 : color.B; alpha++;
            }
            if (alpha == 0) continue;
            var index = (y * size + x) * 4; var coverage = alpha / 16;
            var oldAlpha = pixels[index + 3] / 255d; var nextAlpha = coverage + oldAlpha * (1 - coverage);
            pixels[index] = (byte)Math.Round((blue / 16 + pixels[index] * oldAlpha * (1 - coverage)) / nextAlpha);
            pixels[index + 1] = (byte)Math.Round((green / 16 + pixels[index + 1] * oldAlpha * (1 - coverage)) / nextAlpha);
            pixels[index + 2] = (byte)Math.Round((red / 16 + pixels[index + 2] * oldAlpha * (1 - coverage)) / nextAlpha);
            pixels[index + 3] = (byte)Math.Round(nextAlpha * 255);
        }
        return pixels;
    }
    private static bool Ink(TrayStatusKind kind, double x, double y) => kind switch {
        TrayStatusKind.Ready => Line(x, y, -.48, 0, -.13, .32) || Line(x, y, -.13, .32, .48, -.35),
        TrayStatusKind.Offline => Line(x, y, -.34, -.34, .34, .34) || Line(x, y, -.34, .34, .34, -.34),
        TrayStatusKind.DoNotDisturb => Math.Abs(y) < .11 && Math.Abs(x) < .48,
        TrayStatusKind.Held => Math.Abs(y) < .43 && (Math.Abs(x - .24) < .1 || Math.Abs(x + .24) < .1),
        TrayStatusKind.Muted => Line(x, y, -.42, .42, .42, -.42) || (Math.Abs(x) < .16 && y is > -.45 and < .2) || Line(x, y, 0, .2, 0, .46),
        TrayStatusKind.Call => Math.Abs(x) < .48 && (Math.Abs(x) < .1 && Math.Abs(y) < .45 || Math.Abs(Math.Abs(x) - .32) < .09 && Math.Abs(y) < .25),
        TrayStatusKind.Ringing => (Math.Abs(x) < .32 && y is > -.35 and < .28) || Line(x, y, -.45, .3, .45, .3) || (x * x + (y - .49) * (y - .49) < .025),
        _ => Math.Abs(y) < .1 && (Math.Abs(x) < .1 || Math.Abs(Math.Abs(x) - .38) < .1)
    };
    private static bool Line(double x, double y, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax; var dy = by - ay;
        var t = Math.Clamp(((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy), 0, 1);
        return Math.Pow(x - ax - t * dx, 2) + Math.Pow(y - ay - t * dy, 2) < .013;
    }
}
