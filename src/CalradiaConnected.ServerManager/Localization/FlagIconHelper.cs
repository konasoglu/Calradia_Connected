using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace CoopServerManager.Localization
{
    public sealed class LanguageOption
    {
        public string Code { get; }
        public string NativeName { get; }
        public ImageSource FlagImage { get; }

        public LanguageOption(string code, string nativeName, ImageSource flagImage)
        {
            Code = code;
            NativeName = nativeName;
            FlagImage = flagImage;
        }
    }

    /// <summary>
    /// Generates crisp, high-DPI WPF vector flag icons for all 8 supported languages
    /// (never relies on Windows monochrome emoji letters).
    /// </summary>
    public static class FlagIconHelper
    {
        private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<LanguageOption> BuildLanguageOptions()
        {
            return new List<LanguageOption>
            {
                new("TR", "Türkçe", GetFlag("TR")),
                new("EN", "English", GetFlag("EN")),
                new("PL", "Polski", GetFlag("PL")),
                new("DE", "Deutsch", GetFlag("DE")),
                new("RU", "Русский", GetFlag("RU")),
                new("FR", "Français", GetFlag("FR")),
                new("IT", "Italiano", GetFlag("IT")),
                new("ZH", "简体中文", GetFlag("ZH"))
            };
        }

        public static ImageSource GetFlag(string code)
        {
            if (Cache.TryGetValue(code, out var existing))
            {
                return existing;
            }

            var group = new DrawingGroup();
            const double w = 28.0;
            const double h = 19.0;
            group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, w, h), 2.5, 2.5);

            switch (code.ToUpperInvariant())
            {
                case "TR":
                    AddRect(group, 0, 0, w, h, "#E30A17");
                    // White crescent
                    AddEllipse(group, 10.6, 9.5, 4.7, 4.7, "#FFFFFF");
                    AddEllipse(group, 11.9, 9.5, 3.8, 3.8, "#E30A17");
                    // White 5-point star
                    AddStar(group, 17.5, 9.5, 2.35, 0.95, "#FFFFFF");
                    break;

                case "EN":
                    AddRect(group, 0, 0, w, h, "#012169");
                    // White diagonals
                    AddLine(group, 0, 0, w, h, "#FFFFFF", 3.2);
                    AddLine(group, w, 0, 0, h, "#FFFFFF", 3.2);
                    // Red diagonals
                    AddLine(group, 0, 0, w, h, "#C8102E", 1.4);
                    AddLine(group, w, 0, 0, h, "#C8102E", 1.4);
                    // White central cross
                    AddRect(group, 0, 6.8, w, 5.4, "#FFFFFF");
                    AddRect(group, 11.3, 0, 5.4, h, "#FFFFFF");
                    // Red central cross
                    AddRect(group, 0, 7.9, w, 3.2, "#C8102E");
                    AddRect(group, 12.4, 0, 3.2, h, "#C8102E");
                    break;

                case "PL":
                    AddRect(group, 0, 0, w, h / 2.0, "#FFFFFF");
                    AddRect(group, 0, h / 2.0, w, h / 2.0, "#DC143C");
                    break;

                case "DE":
                    AddRect(group, 0, 0, w, h / 3.0, "#141414");
                    AddRect(group, 0, h / 3.0, w, h / 3.0, "#DD0000");
                    AddRect(group, 0, 2.0 * h / 3.0, w, h / 3.0, "#FFCE00");
                    break;

                case "RU":
                    AddRect(group, 0, 0, w, h / 3.0, "#FFFFFF");
                    AddRect(group, 0, h / 3.0, w, h / 3.0, "#0039A6");
                    AddRect(group, 0, 2.0 * h / 3.0, w, h / 3.0, "#D52B1E");
                    break;

                case "FR":
                    AddRect(group, 0, 0, w / 3.0, h, "#0055A4");
                    AddRect(group, w / 3.0, 0, w / 3.0, h, "#FFFFFF");
                    AddRect(group, 2.0 * w / 3.0, 0, w / 3.0, h, "#EF4135");
                    break;

                case "IT":
                    AddRect(group, 0, 0, w / 3.0, h, "#009246");
                    AddRect(group, w / 3.0, 0, w / 3.0, h, "#FFFFFF");
                    AddRect(group, 2.0 * w / 3.0, 0, w / 3.0, h, "#CE2B37");
                    break;

                case "ZH":
                    AddRect(group, 0, 0, w, h, "#DE2910");
                    AddStar(group, 5.5, 5.3, 2.7, 1.05, "#FFDE00");
                    AddStar(group, 10.6, 2.7, 1.05, 0.45, "#FFDE00");
                    AddStar(group, 12.1, 4.9, 1.05, 0.45, "#FFDE00");
                    AddStar(group, 12.0, 7.6, 1.05, 0.45, "#FFDE00");
                    AddStar(group, 10.3, 9.6, 1.05, 0.45, "#FFDE00");
                    break;

                default:
                    AddRect(group, 0, 0, w, h, "#2563EB");
                    break;
            }

            // Subtle crisp border around flag
            var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), 0.7);
            group.Children.Add(new GeometryDrawing(null, borderPen, new RectangleGeometry(new Rect(0.35, 0.35, w - 0.7, h - 0.7), 2.2, 2.2)));

            var img = new DrawingImage(group);
            img.Freeze();
            Cache[code] = img;
            return img;
        }

        private static SolidColorBrush BrushFromHex(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
            b.Freeze();
            return b;
        }

        private static void AddRect(DrawingGroup g, double x, double y, double w, double h, string hex)
        {
            g.Children.Add(new GeometryDrawing(BrushFromHex(hex), null, new RectangleGeometry(new Rect(x, y, w, h))));
        }

        private static void AddEllipse(DrawingGroup g, double cx, double cy, double rx, double ry, string hex)
        {
            g.Children.Add(new GeometryDrawing(BrushFromHex(hex), null, new EllipseGeometry(new Point(cx, cy), rx, ry)));
        }

        private static void AddLine(DrawingGroup g, double x1, double y1, double x2, double y2, string hex, double thickness)
        {
            var pen = new Pen(BrushFromHex(hex), thickness);
            pen.Freeze();
            g.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(x1, y1), new Point(x2, y2))));
        }

        private static void AddStar(DrawingGroup g, double cx, double cy, double outerR, double innerR, string hex)
        {
            var fig = new PathFigure { IsClosed = true };
            for (int i = 0; i < 10; i++)
            {
                double r = (i % 2 == 0) ? outerR : innerR;
                double angle = -Math.PI / 2.0 + i * Math.PI / 5.0;
                var pt = new Point(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle));
                if (i == 0) fig.StartPoint = pt;
                else fig.Segments.Add(new LineSegment(pt, true));
            }
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            geo.Freeze();
            g.Children.Add(new GeometryDrawing(BrushFromHex(hex), null, geo));
        }
    }
}
