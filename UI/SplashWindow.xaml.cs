using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RevitExcelLink.UI
{
    /// <summary>Page de garde affichée environ 3 secondes au démarrage de Revit (un clic la ferme).</summary>
    public partial class SplashWindow : Window
    {
        private const double TotalSeconds = 3.0;
        private const double FadeOutSeconds = 0.4;
        private bool _closing;

        public SplashWindow(string revitVersion)
        {
            InitializeComponent();
            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            TxtVersion.Text = $"Version {v.Major}.{v.Minor}.{v.Build}  ·  Revit {revitVersion}";
            Loaded += (s, e) => Play();
        }

        public static void ShowFor(string revitVersion)
        {
            try { new SplashWindow(revitVersion).Show(); }
            catch { /* la page de garde ne doit jamais empêcher Revit de démarrer */ }
        }

        private void Play()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            double barWidth = Root.ActualWidth > 0 ? Root.ActualWidth : 640;

            Animate(this, OpacityProperty, 0, 1, 0.0, 0.35, ease);

            Animate(Logo, OpacityProperty, 0, 1, 0.15, 0.7, ease);
            Animate(LogoScale, ScaleTransform.ScaleXProperty, 0.94, 1, 0.15, 0.9, ease);
            Animate(LogoScale, ScaleTransform.ScaleYProperty, 0.94, 1, 0.15, 0.9, ease);

            Animate(BigSigle, OpacityProperty, 0, 0.16, 0.2, 1.4, ease);
            Animate(SigleShift, TranslateTransform.XProperty, 60, 0, 0.2, 1.6, ease);

            Animate(Accent, WidthProperty, 0, 120, 0.6, 0.6, ease);
            Animate(Product, OpacityProperty, 0, 1, 0.8, 0.6, ease);
            Animate(ProductShift, TranslateTransform.YProperty, 10, 0, 0.8, 0.6, ease);

            Animate(Cross1, OpacityProperty, 0, 0.7, 1.0, 0.6, ease);
            Animate(Cross2, OpacityProperty, 0, 0.5, 1.2, 0.6, ease);

            Animate(Progress, WidthProperty, 0, barWidth, 0.1, TotalSeconds - FadeOutSeconds - 0.2,
                    new SineEase { EasingMode = EasingMode.EaseInOut });

            After(1.3, () => TxtStatus.Text = "Préparation du ruban Helora BIM…");
            After(2.3, () => TxtStatus.Text = "Prêt");
            After(TotalSeconds - FadeOutSeconds, FadeOutAndClose);
        }

        private void FadeOutAndClose()
        {
            if (_closing) return;
            _closing = true;
            var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromSeconds(FadeOutSeconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += (s, e) => Close();
            BeginAnimation(OpacityProperty, fade);
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => FadeOutAndClose();

        private static void Animate(IAnimatable target, DependencyProperty prop, double from, double to,
                                    double beginSeconds, double durationSeconds, IEasingFunction ease)
        {
            var anim = new DoubleAnimation(from, to, TimeSpan.FromSeconds(durationSeconds))
            {
                BeginTime = TimeSpan.FromSeconds(beginSeconds),
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };
            target.BeginAnimation(prop, anim);
        }

        private void After(double seconds, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (IsLoaded) action();
            };
            timer.Start();
        }
    }
}
