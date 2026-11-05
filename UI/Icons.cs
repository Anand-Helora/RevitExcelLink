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

        private const string SigleData = "M99.6,86.5L99.6,86.5c2.8,0,5.1-2.3,5.1-5.1V5.1c0-2.8-2.3-5.1-5.1-5.1h0c-2.8,0-5.1,2.3-5.1,5.1v76.2C94.5,84.2,96.8,86.5,99.6,86.5z M81.4,38.2h-33v-33c0-2.8-2.3-5.1-5.1-5.1c-2.8,0-5.1,2.3-5.1,5.1v33h-33c-2.8,0-5.1,2.3-5.1,5.1c0,2.8,2.3,5.1,5.1,5.1h33v33c0,2.8,2.3,5.1,5.1,5.1c2.8,0,5.1-2.3,5.1-5.1v-33h33c2.8,0,5.1-2.3,5.1-5.1S84.3,38.2,81.4,38.2z";

        /// <summary>Icône avec le sigle Helora officiel en blanc sur fond coloré.</summary>
        public static BitmapSource MakeSigle(int size, Color background)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                double radius = size * 0.18;
                dc.DrawRoundedRectangle(new SolidColorBrush(background), null, new Rect(0, 0, size, size), radius, radius);
                Geometry g = Geometry.Parse(SigleData);
                double margin = size * 0.18;
                double scale = (size - 2 * margin) / 104.7;
                var group = new TransformGroup();
                group.Children.Add(new ScaleTransform(scale, scale));
                group.Children.Add(new TranslateTransform(margin, (size - 86.6 * scale) / 2));
                g = g.Clone();
                g.Transform = group;
                dc.DrawGeometry(Brushes.White, null, g);
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
