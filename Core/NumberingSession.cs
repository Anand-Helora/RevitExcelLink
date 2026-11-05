using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitExcelLink.Core
{
    /// <summary>Valeur d'un paramètre avant modification, pour pouvoir la restaurer (« Annuler le dernier »).</summary>
    public sealed class ParamSnapshot
    {
        public ElementId ElementId { get; private set; }
        public string ParamName { get; private set; }     // null = Numéro de la pièce
        public StorageType Storage { get; private set; }
        public object Value { get; private set; }

        public static ParamSnapshot RoomNumber(Room room) =>
            new ParamSnapshot { ElementId = room.Id, ParamName = null, Storage = StorageType.String, Value = room.Number };

        public static ParamSnapshot Take(Element e, string paramName)
        {
            if (string.IsNullOrWhiteSpace(paramName)) return null;
            Parameter p = e.LookupParameter(paramName);
            if (p == null || p.IsReadOnly) return null;
            object v;
            switch (p.StorageType)
            {
                case StorageType.String: v = p.AsString() ?? ""; break;
                case StorageType.Integer: v = p.AsInteger(); break;
                case StorageType.Double: v = p.AsDouble(); break;
                case StorageType.ElementId: v = p.AsElementId(); break;
                default: return null;
            }
            return new ParamSnapshot { ElementId = e.Id, ParamName = paramName, Storage = p.StorageType, Value = v };
        }

        public void Restore(Document doc)
        {
            Element e = doc.GetElement(ElementId);
            if (e == null) return;
            try
            {
                if (ParamName == null)
                {
                    if (e is Room room) room.Number = (string)Value ?? "";
                    return;
                }
                Parameter p = e.LookupParameter(ParamName);
                if (p == null || p.IsReadOnly) return;
                switch (Storage)
                {
                    case StorageType.String: p.Set((string)Value ?? ""); break;
                    case StorageType.Integer: p.Set((int)Value); break;
                    case StorageType.Double: p.Set((double)Value); break;
                    case StorageType.ElementId: p.Set((ElementId)Value); break;
                }
            }
            catch
            {
                // valeur d'origine refusée (ex. numéro vide) : on laisse la valeur actuelle
            }
        }
    }

    /// <summary>Une ligne de l'historique de la palette : local numéroté ou numéros sautés.</summary>
    public sealed class HistoryEntry
    {
        public bool IsSkip { get; set; }
        public int Counter { get; set; }            // numéro attribué (ou premier numéro sauté)
        public int CounterTo { get; set; }          // dernier numéro sauté (plage)
        public string Number { get; set; }
        public string RoomLabel { get; set; }
        public ElementId RoomId { get; set; }
        public string PreviousRoomNumber { get; set; }
        public int ElementCount { get; set; }
        public List<ParamSnapshot> Undo { get; } = new List<ParamSnapshot>();
    }

    /// <summary>
    /// État partagé entre la commande (qui attend les clics dans Revit) et la palette flottante.
    /// La palette ne touche jamais au modèle : elle modifie cet état puis demande à la commande
    /// d'interrompre le clic en cours ; c'est la commande qui agit sur Revit.
    /// </summary>
    public sealed class NumberingSession
    {
        public NumberingSettings Settings { get; }
        public int Current { get; set; }
        public List<HistoryEntry> History { get; } = new List<HistoryEntry>();   // le plus récent en premier

        // Demandes de la palette
        public bool RepickRequested { get; set; }
        public bool UndoRequested { get; set; }
        public bool FinishRequested { get; set; }

        public NumberingSession(NumberingSettings settings)
        {
            Settings = settings;
            Current = settings.Start;
        }

        public string NumberOf(int n) => RoomNumberFormat.Full(Settings.Bloc, Settings.FloorCode, n, Settings.Digits);
        public string CounterOf(int n) => RoomNumberFormat.Counter(n, Settings.Digits);
        public string Prefix => $"{Settings.Bloc}/{Settings.FloorCode}/";

        public int NumberedCount => History.Count(h => !h.IsSkip);
        public bool CanUndo => History.Any(h => !h.IsSkip);

        public void SkipTo(int target)
        {
            if (target > Current)
                History.Insert(0, new HistoryEntry { IsSkip = true, Counter = Current, CounterTo = target - 1 });
            Current = Math.Max(0, target);
        }

        public IEnumerable<int> SkippedNumbers =>
            History.Where(h => h.IsSkip).SelectMany(h => Enumerable.Range(h.Counter, h.CounterTo - h.Counter + 1))
                   .Distinct().OrderBy(n => n);
    }
}
