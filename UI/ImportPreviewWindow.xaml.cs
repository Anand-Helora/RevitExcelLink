using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RevitExcelLink.Core;

namespace RevitExcelLink.UI
{
    public partial class ImportPreviewWindow : Window
    {
        private readonly ICollectionView _view;

        public List<ChangeItem> Items { get; }

        public ImportPreviewWindow(ImportAnalysis analysis, string fileName)
        {
            Items = analysis.Items
                .OrderBy(i => i.Status)
                .ThenBy(i => i.Sheet)
                .ThenBy(i => i.ElementLabel)
                .ThenBy(i => i.ParamName)
                .ToList();

            InitializeComponent();
            DataContext = this;

            _view = CollectionViewSource.GetDefaultView(Items);
            _view.Filter = Filter;

            foreach (ChangeItem item in Items) item.PropertyChanged += (s, e) => UpdateCounts();

            int changes = Items.Count(i => i.Status == ChangeStatus.Change);
            int conflicts = Items.Count(i => i.Status == ChangeStatus.Conflict);
            int problems = Items.Count - changes - conflicts;
            TxtSummary.Text = $"{fileName} : {analysis.RowsRead} ligne(s) lue(s) dans {analysis.SheetsRead} feuille(s) – "
                              + $"{changes} modification(s), {conflicts} conflit(s), {problems} non applicable(s).";
            TxtWarnings.Text = string.Join("\n", analysis.Warnings);
            TxtWarnings.Visibility = analysis.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateCounts();
        }

        private bool Filter(object o)
        {
            var i = (ChangeItem)o;
            switch (CbFilter?.SelectedIndex ?? 0)
            {
                case 1: return i.Status == ChangeStatus.Change;
                case 2: return i.Status == ChangeStatus.Conflict;
                case 3: return !i.CanApply;
                default: return true;
            }
        }

        private void Filter_Changed(object sender, SelectionChangedEventArgs e) => _view?.Refresh();

        private void CheckAll_Click(object sender, RoutedEventArgs e) => SetVisible(true);
        private void UncheckAll_Click(object sender, RoutedEventArgs e) => SetVisible(false);

        private void SetVisible(bool value)
        {
            foreach (ChangeItem i in _view.Cast<ChangeItem>())
                if (i.CanApply) i.Apply = value;
        }

        private void UpdateCounts()
        {
            int n = Items.Count(i => i.Apply);
            BtnApply.Content = $"Appliquer {n} modification(s)";
            BtnApply.IsEnabled = n > 0;
        }

        private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
