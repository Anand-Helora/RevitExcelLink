using System.Collections.Generic;
using System.Linq;
using System.Windows;
using RevitExcelLink.Core;

namespace RevitExcelLink.UI
{
    public sealed class LinkChoice
    {
        public long InstanceId { get; set; }
        public string Name { get; set; }
        public string Detail { get; set; }
        public bool IsChecked { get; set; } = true;
    }

    public enum FillScope { ActiveView, WholeModel, Selection }

    public partial class FillElementsWindow : Window
    {
        public List<LinkChoice> Links { get; }
        public FillScope Scope { get; private set; }
        public bool UseLocalRooms { get; private set; }
        public bool Overwrite { get; private set; }

        public FillElementsWindow(NumberingSettings settings, int localRoomCount, List<LinkChoice> links, bool hasSelection)
        {
            InitializeComponent();
            Links = links;
            LstLinks.ItemsSource = links;

            CbLocal.Content = $"Pièces de ce modèle ({localRoomCount})";
            CbLocal.IsChecked = localRoomCount > 0;
            CbLocal.IsEnabled = localRoomCount > 0;
            CbOverwrite.IsChecked = settings.OverwriteContents;
            if (hasSelection) RbSelection.IsChecked = true;

            TxtParams.Text = $"Paramètres remplis : {settings.ElemParamBloc}, {settings.ElemParamEtage}, "
                             + $"{settings.ElemParamGmao}, {settings.ElemParamLocal}  (modifiables dans la fenêtre de numérotation).";
        }

        private void Run_Click(object sender, RoutedEventArgs e)
        {
            if (CbLocal.IsChecked != true && !Links.Any(l => l.IsChecked))
            {
                TxtError.Text = "Choisissez au moins une source de locaux.";
                TxtError.Visibility = Visibility.Visible;
                return;
            }
            Scope = RbSelection.IsChecked == true ? FillScope.Selection
                  : RbModel.IsChecked == true ? FillScope.WholeModel : FillScope.ActiveView;
            UseLocalRooms = CbLocal.IsChecked == true;
            Overwrite = CbOverwrite.IsChecked == true;
            DialogResult = true;
        }
    }
}
