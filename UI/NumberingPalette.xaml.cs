using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using RevitExcelLink.Core;

namespace RevitExcelLink.UI
{
    /// <summary>
    /// Palette flottante affichée pendant la numérotation. Elle ne modifie jamais le modèle :
    /// elle met à jour la session puis appelle <see cref="_interrupt"/>, qui interrompt le clic en cours
    /// pour que la commande reprenne la main.
    /// </summary>
    public partial class NumberingPalette : Window
    {
        private readonly NumberingSession _session;
        private readonly Action _interrupt;
        private bool _closingFromCommand;

        public NumberingPalette(NumberingSession session, Action interrupt)
        {
            _session = session;
            _interrupt = interrupt;
            InitializeComponent();
            Refresh();
        }

        public void Refresh()
        {
            TxtNumber.Text = _session.NumberOf(_session.Current);
            TxtSub.Text = $"{_session.Settings.ParamGmao} = {_session.CounterOf(_session.Current)}  ·  cliquez le local dans la vue";
            TxtPrefix.Text = _session.Prefix;
            TxtCount.Text = $"{_session.NumberedCount} numéroté(s)";
            BtnUndo.IsEnabled = _session.CanUndo;

            LstHistory.ItemsSource = _session.History.Take(5).Select(h => new
            {
                Text = h.IsSkip
                    ? (h.CounterTo > h.Counter
                        ? $"{_session.CounterOf(h.Counter)} à {_session.CounterOf(h.CounterTo)} · sautés"
                        : $"{h.Number ?? _session.NumberOf(h.Counter)} · sauté")
                    : $"{h.Number} · {h.RoomLabel}" + (h.ElementCount > 0 ? $" · {h.ElementCount} élément(s)" : ""),
                Color = h.IsSkip ? "#E16021" : "#044550"
            }).ToList();
        }

        private void Previous_Click(object sender, RoutedEventArgs e)
        {
            if (_session.Current <= 0) return;
            _session.Current--;
            Changed();
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            _session.History.Insert(0, new HistoryEntry
            {
                IsSkip = true, Counter = _session.Current, CounterTo = _session.Current,
                Number = _session.NumberOf(_session.Current)
            });
            _session.Current++;
            Changed();
        }

        private void GoTo_Click(object sender, RoutedEventArgs e) => GoTo();

        private void GoTo_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { GoTo(); e.Handled = true; }
        }

        private void GoTo()
        {
            if (!int.TryParse(TxtGoTo.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int target) || target < 0)
            {
                TxtError.Text = "Saisissez un numéro (ex. 20).";
                TxtError.Visibility = Visibility.Visible;
                return;
            }
            TxtError.Visibility = Visibility.Collapsed;
            TxtGoTo.Text = "";
            _session.SkipTo(target);
            Changed();
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (!_session.CanUndo) return;
            _session.UndoRequested = true;
            _interrupt();
        }

        private void Finish_Click(object sender, RoutedEventArgs e)
        {
            _session.FinishRequested = true;
            _interrupt();
        }

        private void Changed()
        {
            Refresh();
            _session.RepickRequested = true;
            _interrupt();
        }

        /// <summary>Fermeture par la commande (fin de session), sans redemander d'interruption.</summary>
        public void CloseFromCommand()
        {
            _closingFromCommand = true;
            Close();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_closingFromCommand) return;
            // Croix de la palette = Terminer
            _session.FinishRequested = true;
            _interrupt();
        }
    }
}
