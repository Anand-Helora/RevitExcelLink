using System.Globalization;
using System.Windows;

namespace RevitExcelLink.UI
{
    public partial class NumberInputWindow : Window
    {
        public int Value { get; private set; }

        public NumberInputWindow(string prefix, int current, string info)
        {
            InitializeComponent();
            TxtPrefix.Text = prefix;
            TxtInfo.Text = info;
            TxtNumber.Text = current.ToString(CultureInfo.InvariantCulture);
            Loaded += (s, e) => { TxtNumber.Focus(); TxtNumber.SelectAll(); };
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtNumber.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < 0)
            {
                TxtError.Text = "Saisissez un nombre entier positif.";
                TxtError.Visibility = Visibility.Visible;
                return;
            }
            Value = v;
            DialogResult = true;
        }
    }
}
