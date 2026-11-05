using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitExcelLink.UI
{
    /// <summary>Icônes du ruban dessinées en code (aucun fichier image à fournir).</summary>
    internal static class Icons
    {
        public static BitmapSource Make(int size, string glyph, Color background)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                double radius = size * 0.18;
                dc.DrawRoundedRectangle(new SolidColorBrush(background), null, new Rect(0, 0, size, size), radius, radius);

                var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI Symbol"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    size * 0.55, Brushes.White, 1.0);
                dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
            }

            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        public static readonly Color HeloraTurquoise = Color.FromRgb(0x00, 0x9E, 0x99);
        public static readonly Color HeloraDark = Color.FromRgb(0x04, 0x45, 0x50);
    }
}
