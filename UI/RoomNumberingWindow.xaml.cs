using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RevitExcelLink.Core;

namespace RevitExcelLink.UI
{
    public partial class RoomNumberingWindow : Window
    {
        private readonly List<string> _existingNumbers;
        private bool _floorCodeEditedByUser;
        private bool _updating;
        private int _suggestedNext;

        public NumberingSettings Settings { get; }

        public RoomNumberingWindow(NumberingSettings settings, IEnumerable<string> existingNumbers)
        {
            Settings = settings;
            _existingNumbers = existingNumbers.ToList();
            InitializeComponent();

            _updating = true;
            TxtBloc.Text = settings.Bloc;
            TxtEtage.Text = settings.Etage;
            _floorCodeEditedByUser = !string.IsNullOrWhiteSpace(settings.FloorCode)
                                     && settings.FloorCode != RoomNumberFormat.FloorCode(settings.Etage);
            TxtFloorCode.Text = _floorCodeEditedByUser ? settings.FloorCode : RoomNumberFormat.FloorCode(settings.Etage);
            TxtStart.Text = settings.Start.ToString(CultureInfo.InvariantCulture);
            CbDigits.SelectedIndex = settings.Digits <= 2 ? 0 : settings.Digits >= 4 ? 2 : 1;
            TxtParamBloc.Text = settings.ParamBloc;
            TxtParamEtage.Text = settings.ParamEtage;
            TxtParamGmao.Text = settings.ParamGmao;
            CbFillContents.IsChecked = settings.FillContents;
            TxtElemBloc.Text = settings.ElemParamBloc;
            TxtElemEtage.Text = settings.ElemParamEtage;
            TxtElemGmao.Text = settings.ElemParamGmao;
            TxtElemLocal.Text = settings.ElemParamLocal;
            _updating = false;

            Loaded += (s, e) => { TxtBloc.Focus(); TxtBloc.SelectAll(); };
            UpdatePreview();
        }

        private int Digits => CbDigits.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int d) ? d : 3;

        private void Etage_Changed(object sender, TextChangedEventArgs e)
        {
            if (_updating) return;
            if (!_floorCodeEditedByUser)
            {
                _updating = true;
                TxtFloorCode.Text = RoomNumberFormat.FloorCode(TxtEtage.Text);
                _updating = false;
            }
            UpdatePreview();
        }

        private void FloorCode_Changed(object sender, TextChangedEventArgs e)
        {
            if (_updating) return;
            // Code effacé = retour au calcul automatique
            _floorCodeEditedByUser = !string.IsNullOrWhiteSpace(TxtFloorCode.Text)
                                     && TxtFloorCode.Text.Trim() != RoomNumberFormat.FloorCode(TxtEtage.Text);
            UpdatePreview();
        }

        private void Field_Changed(object sender, RoutedEventArgs e)
        {
            if (!_updating) UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (TxtPreview == null) return;
            string bloc = TxtBloc.Text.Trim();
            string code = TxtFloorCode.Text.Trim();
            bool startOk = int.TryParse(TxtStart.Text.Trim(), out int start) && start >= 0;

            if (bloc.Length == 0 || code.Length == 0 || !startOk)
            {
                TxtPreview.Text = "—";
                TxtPreviewDetail.Text = "Renseignez le bloc, l'étage et le numéro de départ.";
                LastUsedPanel.Visibility = Visibility.Collapsed;
                return;
            }

            string number = RoomNumberFormat.Full(bloc, code, start, Digits);
            TxtPreview.Text = number;
            TxtPreviewDetail.Text = $"{Settings.ParamBloc} = {bloc}   ·   {Settings.ParamEtage} = {TxtEtage.Text.Trim()}   ·   "
                                    + $"{Settings.ParamGmao} = {RoomNumberFormat.Counter(start, Digits)}";

            // Dernier numéro déjà utilisé sur ce bloc / étage
            string prefix = $"{bloc}/{code}/";
            int last = RoomNumberFormat.LastUsed(_existingNumbers, prefix);
            if (last > 0)
            {
                _suggestedNext = last + 1;
                TxtLastUsed.Text = $"Dernier numéro utilisé sur {bloc}/{code} : {RoomNumberFormat.Counter(last, Digits)}";
                BtnContinue.Content = $"Continuer à {RoomNumberFormat.Counter(_suggestedNext, Digits)}";
                BtnContinue.Visibility = start == _suggestedNext ? Visibility.Collapsed : Visibility.Visible;
                LastUsedPanel.Visibility = Visibility.Visible;
            }
            else
            {
                LastUsedPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void Continue_Click(object sender, RoutedEventArgs e) =>
            TxtStart.Text = _suggestedNext.ToString(CultureInfo.InvariantCulture);

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            string bloc = TxtBloc.Text.Trim();
            string etage = TxtEtage.Text.Trim();
            string code = TxtFloorCode.Text.Trim();

            string error = null;
            if (bloc.Length == 0) error = "Indiquez le bloc.";
            else if (!int.TryParse(etage, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) error = "L'étage doit être un nombre entier (ex. 0, 2 ou -1).";
            else if (code.Length == 0) error = "Indiquez le code étage.";
            else if (!int.TryParse(TxtStart.Text.Trim(), out int s) || s < 0) error = "Le numéro de départ doit être un nombre positif.";
            else if (bloc.Contains("/") || code.Contains("/")) error = "Le bloc et le code étage ne peuvent pas contenir « / ».";

            if (error != null)
            {
                TxtError.Text = error;
                TxtError.Visibility = Visibility.Visible;
                return;
            }

            Settings.Bloc = bloc;
            Settings.Etage = etage;
            Settings.FloorCode = code;
            Settings.Start = int.Parse(TxtStart.Text.Trim(), CultureInfo.InvariantCulture);
            Settings.Digits = Digits;
            Settings.ParamBloc = TxtParamBloc.Text.Trim();
            Settings.ParamEtage = TxtParamEtage.Text.Trim();
            Settings.ParamGmao = TxtParamGmao.Text.Trim();
            Settings.FillContents = CbFillContents.IsChecked == true;
            Settings.ElemParamBloc = TxtElemBloc.Text.Trim();
            Settings.ElemParamEtage = TxtElemEtage.Text.Trim();
            Settings.ElemParamGmao = TxtElemGmao.Text.Trim();
            Settings.ElemParamLocal = TxtElemLocal.Text.Trim();
            DialogResult = true;
        }
    }
}
