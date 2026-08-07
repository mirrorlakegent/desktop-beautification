using System;
using System.Globalization;
using System.Windows.Media;

namespace DesktopSuite.Themes;

/// <summary>
/// Perceptually uniform color space used by the design system.
/// All interpolation, derivation and contrast fixing happens here.
/// </summary>
public readonly record struct Oklch(double L, double C, double H, double Alpha = 1.0);

public readonly record struct RgbByte(byte R, byte G, byte B, byte A = 255);

public static class ColorEngine
{
    // XYZ (D65) -> linear LMS, then cube-root for OKLab.
    private static readonly double[,] XyzToLms = new[,]
    {
        { 0.8190224379967030, 0.3619062600528904, -0.1288737815209879 },
        { 0.0329836539323885, 0.9292868615863434, 0.0361446663506424 },
        { 0.0481771893596242, 0.2642395317527308, 0.6335478284694309 }
    };

    private static readonly double[,] LmsToLab = new[,]
    {
        { 0.2104542553, 0.7936177850, -0.0040720468 },
        { 1.9779984951, -2.4285922050, 0.4505937099 },
        { 0.0259040371, 0.7827717662, -0.8086757660 }
    };

    private static readonly double[,] LabToLms = new[,]
    {
        { 1.0, 0.3963377774, 0.2158037573 },
        { 1.0, -0.1055613458, -0.0638541728 },
        { 1.0, -0.0894841775, -1.2914855480 }
    };

    private static readonly double[,] LmsToXyz = new[,]
    {
        { 1.2268798758459243, -0.5578149944602171, 0.2813910456659647 },
        { -0.0405757452148008, 1.1122868032803170, -0.0717110580655164 },
        { -0.0763729366746601, -0.4214933324022432, 1.5869240198367816 }
    };

    private const double Epsilon = 0.00001;

    public static RgbByte HexToRgb(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#')
            throw new FormatException($"Invalid hex color: {hex}");

        ReadOnlySpan<char> span = hex.AsSpan(1);
        byte a = 255, r, g, b;

        if (span.Length == 6)
        {
            r = byte.Parse(span.Slice(0, 2), NumberStyles.HexNumber);
            g = byte.Parse(span.Slice(2, 2), NumberStyles.HexNumber);
            b = byte.Parse(span.Slice(4, 2), NumberStyles.HexNumber);
        }
        else if (span.Length == 8)
        {
            // Schema specifies #RRGGBBAA (CSS Color Module Level 4 style).
            r = byte.Parse(span.Slice(0, 2), NumberStyles.HexNumber);
            g = byte.Parse(span.Slice(2, 2), NumberStyles.HexNumber);
            b = byte.Parse(span.Slice(4, 2), NumberStyles.HexNumber);
            a = byte.Parse(span.Slice(6, 2), NumberStyles.HexNumber);
        }
        else
        {
            throw new FormatException($"Hex color must be #RRGGBB or #RRGGBBAA: {hex}");
        }

        return new RgbByte(r, g, b, a);
    }

    public static string RgbToHex(RgbByte rgb)
    {
        if (rgb.A == 255)
            return $"#{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}";
        return $"#{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}{rgb.A:X2}";
    }

    public static Oklch RgbToOklch(RgbByte rgb)
    {
        double rl = SrgbToLinear(rgb.R / 255.0);
        double gl = SrgbToLinear(rgb.G / 255.0);
        double bl = SrgbToLinear(rgb.B / 255.0);

        // sRGB D65 -> XYZ D65
        double x = 0.4124564 * rl + 0.3575761 * gl + 0.1804375 * bl;
        double y = 0.2126729 * rl + 0.7151522 * gl + 0.0721750 * bl;
        double z = 0.0193339 * rl + 0.1191920 * gl + 0.9503041 * bl;

        double l = XyzToLms[0, 0] * x + XyzToLms[0, 1] * y + XyzToLms[0, 2] * z;
        double m = XyzToLms[1, 0] * x + XyzToLms[1, 1] * y + XyzToLms[1, 2] * z;
        double s = XyzToLms[2, 0] * x + XyzToLms[2, 1] * y + XyzToLms[2, 2] * z;

        double lc = Math.Cbrt(l);
        double mc = Math.Cbrt(m);
        double sc = Math.Cbrt(s);

        double L = LmsToLab[0, 0] * lc + LmsToLab[0, 1] * mc + LmsToLab[0, 2] * sc;
        double a = LmsToLab[1, 0] * lc + LmsToLab[1, 1] * mc + LmsToLab[1, 2] * sc;
        double b = LmsToLab[2, 0] * lc + LmsToLab[2, 1] * mc + LmsToLab[2, 2] * sc;

        double C = Math.Sqrt(a * a + b * b);
        double H = C < Epsilon ? 0 : Math.Atan2(b, a) * 180.0 / Math.PI;
        if (H < 0) H += 360;

        return new Oklch(L, C, H, rgb.A / 255.0);
    }

    public static RgbByte OklchToRgb(Oklch oklch)
    {
        double a = oklch.C * Math.Cos(oklch.H * Math.PI / 180.0);
        double b = oklch.C * Math.Sin(oklch.H * Math.PI / 180.0);

        double lc = LabToLms[0, 0] * oklch.L + LabToLms[0, 1] * a + LabToLms[0, 2] * b;
        double mc = LabToLms[1, 0] * oklch.L + LabToLms[1, 1] * a + LabToLms[1, 2] * b;
        double sc = LabToLms[2, 0] * oklch.L + LabToLms[2, 1] * a + LabToLms[2, 2] * b;

        double l = lc * lc * lc;
        double m = mc * mc * mc;
        double s = sc * sc * sc;

        double x = LmsToXyz[0, 0] * l + LmsToXyz[0, 1] * m + LmsToXyz[0, 2] * s;
        double y = LmsToXyz[1, 0] * l + LmsToXyz[1, 1] * m + LmsToXyz[1, 2] * s;
        double z = LmsToXyz[2, 0] * l + LmsToXyz[2, 1] * m + LmsToXyz[2, 2] * s;

        double rl = 3.2404542 * x + -1.5371385 * y + -0.4985314 * z;
        double gl = -0.9692660 * x + 1.8760108 * y + 0.0415560 * z;
        double bl = 0.0556434 * x + -0.2040259 * y + 1.0572252 * z;

        byte r = (byte)Math.Round(LinearToSrgb(rl) * 255.0);
        byte g = (byte)Math.Round(LinearToSrgb(gl) * 255.0);
        byte bb = (byte)Math.Round(LinearToSrgb(bl) * 255.0);
        byte alpha = (byte)Math.Round(Math.Clamp(oklch.Alpha, 0.0, 1.0) * 255.0);

        return new RgbByte(r, g, bb, alpha);
    }

    public static Color ToMediaColor(RgbByte rgb) =>
        Color.FromArgb(rgb.A, rgb.R, rgb.G, rgb.B);

    public static Brush ToBrush(RgbByte rgb) =>
        new SolidColorBrush(ToMediaColor(rgb));

    public static Brush ToBrush(Oklch oklch) =>
        ToBrush(OklchToRgb(oklch));

    public static Oklch WithLightness(this Oklch c, double l) =>
        new(Math.Clamp(l, 0, 1), c.C, c.H, c.Alpha);

    public static Oklch WithAlpha(this Oklch c, double a) =>
        new(c.L, c.C, c.H, Math.Clamp(a, 0, 1));

    private static double SrgbToLinear(double v) =>
        v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    private static double LinearToSrgb(double v) =>
        v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;
}
