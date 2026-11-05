using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using RevitExcelLink.Core;
using DB = Autodesk.Revit.DB;
using UIDocument = Autodesk.Revit.UI.UIDocument;

namespace RevitExcelLink.UI
{
    public sealed class CategoryItem : INotifyPropertyChanged
    {
        private bool _isChecked;
        public CategoryData Data { get; }
        public Action Changed { get; set; }

        public CategoryItem(CategoryData data) { Data = data; }

        public string Display => $"{Data.Name}  ({Data.ElementIds.Count})" + (Data.IsAnnotation ? "  – annotation" : "");

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                Changed?.Invoke();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public sealed class ParamItem
    {
        private static readonly Brush InstanceBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)));
        private static readonly Brush TypeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)));
        private static readonly Brush ReadOnlyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73)));

        public ParamDescriptor Desc { get; }
        public ParamItem(ParamDescriptor d) { Desc = d; }

        public string Display => Desc.Name + (Desc.Owner == ParamOwner.Type ? "  (type)" : "");
        public Brush Brush => Desc.IsReadOnly ? ReadOnlyBrush : Desc.Owner == ParamOwner.Type ? TypeBrush : InstanceBrush;
        public string Tooltip => $"{Desc.Origin}\n{(Desc.Owner == ParamOwner.Type ? "Paramètre de type" : "Paramètre d'occurrence")}"
                                 + (Desc.IsReadOnly ? "\nLecture seule" : "");

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    }

    public partial class ExportWindow : Window
    {
        private readonly UIDocument _uidoc;
        private readonly DB.Document _doc;
        private bool _suspendRefresh;
        private ICollectionView _catView;
        private ICollectionView _availView;

        public ObservableCollection<CategoryItem> Categories { get; } = new ObservableCollection<CategoryItem>();
        public ObservableCollection<ParamItem> Available { get; } = new ObservableCollection<ParamItem>();
        public ObservableCollection<ParamItem> Selected { get; } = new ObservableCollection<ParamItem>();

        /// <summary>Demande d'export, renseignée quand l'utilisateur valide.</summary>
        public ExportRequest Result { get; private set; }

        public ExportWindow(UIDocument uidoc)
        {
            _uidoc = uidoc;
            _doc = uidoc.Document;
            InitializeComponent();
            DataContext = this;

            _catView = CollectionViewSource.GetDefaultView(Categories);
            _catView.Filter = o => Matches(((CategoryItem)o).Data.Name, TxtCatSearch.Text);

            _availView = CollectionViewSource.GetDefaultView(Available);
            _availView.SortDescriptions.Add(new SortDescription(nameof(ParamItem.Display), ListSortDirection.Ascending));
            _availView.Filter = o => FilterParam((ParamItem)o);

            // Une sélection dans Revit = portée « Sélection courante » par défaut
            if (_uidoc.Selection.GetElementIds().Count > 0) RbSelection.IsChecked = true;
            else LoadCategories();
        }

        private ExportScope CurrentScope =>
            RbSelection.IsChecked == true ? ExportScope.Selection :
            RbView.IsChecked == true ? ExportScope.ActiveView : ExportScope.WholeModel;

        // ---------- Catégories ----------

        private void LoadCategories()
        {
            var previouslyChecked = new HashSet<long>(Categories.Where(c => c.IsChecked).Select(c => c.Data.Id));
            _suspendRefresh = true;
            Categories.Clear();
            foreach (CategoryData data in ModelScanner.CollectCategories(_uidoc, CurrentScope))
            {
                var item = new CategoryItem(data) { IsChecked = previouslyChecked.Contains(data.Id) };
                item.Changed = OnCategoryChanged;
                Categories.Add(item);
            }
            _suspendRefresh = false;
            RefreshAvailable();
        }

        private void OnCategoryChanged()
        {
            if (!_suspendRefresh) RefreshAvailable();
        }

        private void Scope_Changed(object sender, RoutedEventArgs e)
        {
            if (IsLoaded || sender == RbSelection) LoadCategories();
        }

        private void ByType_Changed(object sender, RoutedEventArgs e)
        {
            CbShowInstance.IsEnabled = CbByType.IsChecked != true;
            RefreshAvailable();
        }

        private void CatSearch_Changed(object sender, TextChangedEventArgs e) => _catView.Refresh();

        private void CheckAllCats_Click(object sender, RoutedEventArgs e) => SetVisibleCategories(true);
        private void UncheckAllCats_Click(object sender, RoutedEventArgs e) => SetVisibleCategories(false);

        private void SetVisibleCategories(bool value)
        {
            _suspendRefresh = true;
            foreach (CategoryItem c in _catView.Cast<CategoryItem>()) c.IsChecked = value;
            _suspendRefresh = false;
            RefreshAvailable();
        }

        // ---------- Paramètres ----------

        private void RefreshAvailable()
        {
            if (Available == null || Selected == null) return;
            bool byType = CbByType.IsChecked == true;
            var cats = Categories.Where(c => c.IsChecked).Select(c => c.Data).ToList();

            List<ParamDescriptor> descs;
            using (new WaitCursor())
                descs = cats.Count == 0 ? new List<ParamDescriptor>() : ModelScanner.CollectParameters(_doc, cats, byType);

            var byId = descs.ToDictionary(d => d.Id);

            // Conserver l'ordre des colonnes choisies, en retirant celles qui n'existent plus
            var keep = Selected.Where(s => byId.ContainsKey(s.Desc.Id)).Select(s => new ParamItem(byId[s.Desc.Id])).ToList();
            Selected.Clear();
            foreach (ParamItem p in keep) Selected.Add(p);

            var selectedIds = new HashSet<string>(keep.Select(k => k.Desc.Id));
            Available.Clear();
            foreach (ParamDescriptor d in descs.Where(d => !selectedIds.Contains(d.Id)))
                Available.Add(new ParamItem(d));

            UpdateStatus();
        }

        private bool FilterParam(ParamItem p)
        {
            if (!Matches(p.Desc.Name, TxtParamSearch.Text)) return false;
            if (p.Desc.IsReadOnly && CbShowReadOnly.IsChecked != true) return false;
            if (p.Desc.Owner == ParamOwner.Type && CbShowType.IsChecked != true) return false;
            if (p.Desc.Owner == ParamOwner.Instance && CbShowInstance.IsChecked != true) return false;
            return true;
        }

        private void ParamSearch_Changed(object sender, TextChangedEventArgs e) => _availView.Refresh();
        private void ParamFilter_Click(object sender, RoutedEventArgs e) => _availView.Refresh();

        private void Add_Click(object sender, RoutedEventArgs e) => Move(LstAvailable.SelectedItems.Cast<ParamItem>().ToList(), Available, Selected);
        private void Remove_Click(object sender, RoutedEventArgs e) => Move(LstSelected.SelectedItems.Cast<ParamItem>().ToList(), Selected, Available);
        private void AddAll_Click(object sender, RoutedEventArgs e) => Move(_availView.Cast<ParamItem>().ToList(), Available, Selected);
        private void RemoveAll_Click(object sender, RoutedEventArgs e) => Move(Selected.ToList(), Selected, Available);

        private void Available_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (LstAvailable.SelectedItem is ParamItem p) Move(new List<ParamItem> { p }, Available, Selected);
        }

        private void Selected_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (LstSelected.SelectedItem is ParamItem p) Move(new List<ParamItem> { p }, Selected, Available);
        }

        private void Move(List<ParamItem> items, ObservableCollection<ParamItem> from, ObservableCollection<ParamItem> to)
        {
            foreach (ParamItem p in items)
            {
                from.Remove(p);
                to.Add(p);
            }
            UpdateStatus();
        }

        private void Up_Click(object sender, RoutedEventArgs e) => Shift(-1);
        private void Down_Click(object sender, RoutedEventArgs e) => Shift(1);

        private void Shift(int delta)
        {
            var items = LstSelected.SelectedItems.Cast<ParamItem>()
                .OrderBy(p => Selected.IndexOf(p) * delta).Reverse().ToList();
            foreach (ParamItem p in items)
            {
                int i = Selected.IndexOf(p);
                int j = i + delta;
                if (j < 0 || j >= Selected.Count || items.Contains(Selected[j])) continue;
                Selected.Move(i, j);
            }
        }

        private void UpdateStatus()
        {
            int cats = Categories.Count(c => c.IsChecked);
            int elems = Categories.Where(c => c.IsChecked).Sum(c => c.Data.ElementIds.Count);
            TxtStatus.Text = Categories.Count == 0
                ? "Aucun élément dans cette portée."
                : $"{cats} catégorie(s), {elems} élément(s), {Selected.Count} colonne(s)";
        }

        // ---------- Profils ----------

        private void SaveProfile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Profil d'export (*.json)|*.json",
                InitialDirectory = ExportProfile.Folder,
                FileName = "profil.json"
            };
            if (dlg.ShowDialog(this) != true) return;

            var profile = new ExportProfile
            {
                Scope = CurrentScope.ToString(),
                ByType = CbByType.IsChecked == true,
                CategoryIds = Categories.Where(c => c.IsChecked).Select(c => c.Data.Id).ToList(),
                CategoryNames = Categories.Where(c => c.IsChecked).Select(c => c.Data.Name).ToList(),
                Parameters = Selected.Select(p => p.Desc.Id).ToList()
            };
            profile.Save(dlg.FileName);
        }

        private void LoadProfile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Profil d'export (*.json)|*.json",
                InitialDirectory = ExportProfile.Folder
            };
            if (dlg.ShowDialog(this) != true) return;

            ExportProfile profile;
            try { profile = ExportProfile.Load(dlg.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Profil illisible : " + ex.Message, "Profil", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _suspendRefresh = true;
            Enum.TryParse(profile.Scope, out ExportScope scope);
            RbModel.IsChecked = scope == ExportScope.WholeModel;
            RbView.IsChecked = scope == ExportScope.ActiveView;
            RbSelection.IsChecked = scope == ExportScope.Selection;
            CbByType.IsChecked = profile.ByType;
            _suspendRefresh = false;
            LoadCategories();

            _suspendRefresh = true;
            var ids = new HashSet<long>(profile.CategoryIds ?? new List<long>());
            var names = new HashSet<string>(profile.CategoryNames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (CategoryItem c in Categories)
                c.IsChecked = ids.Contains(c.Data.Id) || names.Contains(c.Data.Name);
            _suspendRefresh = false;
            RefreshAvailable();

            var missing = new List<string>();
            foreach (string id in profile.Parameters ?? new List<string>())
            {
                ParamItem p = Available.FirstOrDefault(a => a.Desc.Id == id);
                if (p != null) { Available.Remove(p); Selected.Add(p); }
                else missing.Add(id);
            }
            UpdateStatus();
            if (missing.Count > 0)
                TxtStatus.Text += $" – {missing.Count} paramètre(s) du profil absent(s) de ce projet";
        }

        // ---------- Validation ----------

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var cats = Categories.Where(c => c.IsChecked).Select(c => c.Data).ToList();
            if (cats.Count == 0) { Warn("Cochez au moins une catégorie."); return; }
            if (Selected.Count == 0) { Warn("Ajoutez au moins un paramètre dans la liste de droite."); return; }

            string safeTitle = string.Concat(_doc.Title.Split(Path.GetInvalidFileNameChars()));
            var dlg = new SaveFileDialog
            {
                Filter = "Classeur Excel (*.xlsx)|*.xlsx",
                FileName = $"{safeTitle}_export_{DateTime.Now:yyyyMMdd_HHmm}.xlsx",
                InitialDirectory = UserSettings.LastFolder
            };
            if (dlg.ShowDialog(this) != true) return;
            UserSettings.LastFolder = Path.GetDirectoryName(dlg.FileName);

            Result = new ExportRequest
            {
                Categories = cats,
                Parameters = Selected.Select(p => p.Desc).ToList(),
                ByType = CbByType.IsChecked == true,
                FilePath = dlg.FileName,
                OpenAfter = CbOpen.IsChecked == true
            };
            DialogResult = true;
        }

        private void Warn(string msg) => MessageBox.Show(this, msg, "Exporter vers Excel", MessageBoxButton.OK, MessageBoxImage.Information);

        private static bool Matches(string value, string search) =>
            string.IsNullOrWhiteSpace(search) || (value ?? "").IndexOf(search.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0;
    }

    /// <summary>Curseur d'attente pendant les opérations longues.</summary>
    internal sealed class WaitCursor : IDisposable
    {
        private readonly System.Windows.Input.Cursor _previous;
        public WaitCursor()
        {
            _previous = System.Windows.Input.Mouse.OverrideCursor;
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        }
        public void Dispose() => System.Windows.Input.Mouse.OverrideCursor = _previous;
    }

    /// <summary>Petits réglages mémorisés entre deux utilisations.</summary>
    internal static class UserSettings
    {
        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitExcelLink", "dernier_dossier.txt");

        public static string LastFolder
        {
            get
            {
                try
                {
                    string dir = File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim() : null;
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                }
                catch { }
                return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }
            set
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.WriteAllText(FilePath, value ?? "");
                }
                catch { }
            }
        }
    }
}
