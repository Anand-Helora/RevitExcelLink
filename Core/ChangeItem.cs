using System.ComponentModel;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;

namespace RevitExcelLink.Core
{
    public enum ChangeStatus
    {
        Change,     // modification applicable
        Conflict,   // modifiée dans Excel ET dans Revit depuis l'export
        ReadOnly,   // paramètre en lecture seule
        Error,      // valeur invalide, ambiguë…
        Missing     // élément ou paramètre introuvable
    }

    /// <summary>Une modification détectée dans le fichier Excel (une ligne de l'aperçu).</summary>
    public sealed class ChangeItem : INotifyPropertyChanged
    {
        private bool _apply;
        private string _result;

        public string Sheet { get; set; }
        public string ElementIdText { get; set; }
        public string ElementLabel { get; set; }
        public string ParamName { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
        public string Message { get; set; }
        public ChangeStatus Status { get; set; }

        // Données techniques pour l'application
        public string TargetUid { get; set; }
        public string ParamKey { get; set; }
        public StorageType Storage { get; set; }
        public object Value { get; set; }

        public bool CanApply => Status == ChangeStatus.Change || Status == ChangeStatus.Conflict;

        public bool Apply
        {
            get => _apply;
            set { if (_apply != value && (CanApply || !value)) { _apply = value; OnChanged(); } }
        }

        /// <summary>Résultat après application (vide avant).</summary>
        public string Result
        {
            get => _result;
            set { _result = value; OnChanged(); }
        }

        public bool Succeeded { get; set; }

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case ChangeStatus.Change: return "Modification";
                    case ChangeStatus.Conflict: return "Conflit";
                    case ChangeStatus.ReadOnly: return "Lecture seule";
                    case ChangeStatus.Error: return "Erreur";
                    default: return "Introuvable";
                }
            }
        }

        public string StatusColor
        {
            get
            {
                switch (Status)
                {
                    case ChangeStatus.Change: return "#009E99";    // turquoise Helora
                    case ChangeStatus.Conflict: return "#E16021";  // orange Helora (signal)
                    case ChangeStatus.ReadOnly: return "#7A8B8E";
                    default: return "#B3261E";
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
