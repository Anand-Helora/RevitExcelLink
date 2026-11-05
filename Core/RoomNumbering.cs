using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitExcelLink.Core
{
    /// <summary>Réglages de la numérotation, mémorisés entre deux utilisations.</summary>
    public sealed class NumberingSettings
    {
        public string Bloc { get; set; } = "A";
        public string Etage { get; set; } = "0";
        public string FloorCode { get; set; } = "";     // vide = calcul automatique
        public int Start { get; set; } = 1;
        public int Digits { get; set; } = 3;

        // Noms des paramètres Helora
        public string ParamBloc { get; set; } = "HEL_BLOC";
        public string ParamEtage { get; set; } = "HEL_Etage";
        public string ParamGmao { get; set; } = "H_Num_GMAO";

        // Éléments contenus dans le local
        public bool FillContents { get; set; } = true;
        public bool OverwriteContents { get; set; } = true;
        public string ElemParamBloc { get; set; } = "H_Bloc";
        public string ElemParamEtage { get; set; } = "H_Etage";
        public string ElemParamGmao { get; set; } = "H_Num_GMAO";
        public string ElemParamLocal { get; set; } = "NUM_LOCAL_GMAO";

        // Position de la palette (-1 = position par défaut)
        public double PaletteLeft { get; set; } = -1;
        public double PaletteTop { get; set; } = -1;

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitExcelLink", "numerotation.json");

        public static NumberingSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<NumberingSettings>(File.ReadAllText(FilePath)) ?? new NumberingSettings();
            }
            catch { }
            return new NumberingSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    /// <summary>Format des numéros de locaux Helora : BLOC/CODE-ÉTAGE/NUMÉRO, ex. B/N00/015.</summary>
    public static class RoomNumberFormat
    {
        /// <summary>0 → N00, 2 → N02, -1 → S-1, -3 → S-3.</summary>
        public static string FloorCode(string etage)
        {
            if (!int.TryParse((etage ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return "";
            return n >= 0 ? "N" + n.ToString("00") : "S-" + (-n).ToString(CultureInfo.InvariantCulture);
        }

        public static string Counter(int number, int digits) => number.ToString(new string('0', Math.Max(1, digits)));

        public static string Full(string bloc, string floorCode, int number, int digits) =>
            $"{bloc.Trim()}/{floorCode.Trim()}/{Counter(number, digits)}";

        /// <summary>Plus grand numéro déjà utilisé avec ce préfixe (ex. « B/N00/ »), ou 0.</summary>
        public static int LastUsed(IEnumerable<string> numbers, string prefix)
        {
            int max = 0;
            foreach (string n in numbers)
            {
                if (n == null || !n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(n.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > max)
                    max = v;
            }
            return max;
        }
    }

    /// <summary>Numéros de pièces existants, pour repérer les doublons.</summary>
    public sealed class RoomNumberIndex
    {
        private readonly Dictionary<string, List<ElementId>> _byNumber = new Dictionary<string, List<ElementId>>(StringComparer.OrdinalIgnoreCase);

        public RoomNumberIndex(Document doc)
        {
            foreach (Room r in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>())
                Add(r.Number, r.Id);
        }

        public IEnumerable<string> AllNumbers => _byNumber.Keys;

        /// <summary>Autres pièces qui portent déjà ce numéro.</summary>
        public List<ElementId> UsedBy(string number, ElementId except) =>
            _byNumber.TryGetValue(number ?? "", out var ids) ? ids.Where(id => id != except).ToList() : new List<ElementId>();

        public void Move(ElementId roomId, string oldNumber, string newNumber)
        {
            if (oldNumber != null && _byNumber.TryGetValue(oldNumber, out var ids)) ids.Remove(roomId);
            Add(newNumber, roomId);
        }

        private void Add(string number, ElementId id)
        {
            if (string.IsNullOrEmpty(number)) return;
            if (!_byNumber.TryGetValue(number, out var ids)) _byNumber[number] = ids = new List<ElementId>();
            if (!ids.Contains(id)) ids.Add(id);
        }
    }

    public static class RoomParameterWriter
    {
        /// <summary>Écrit une valeur dans un paramètre texte, entier ou numérique. Renvoie un message d'erreur ou null.</summary>
        public static string Set(Element e, string paramName, string text)
        {
            Parameter p = e.LookupParameter(paramName);
            if (p == null) return $"paramètre « {paramName} » absent";
            if (p.IsReadOnly) return $"paramètre « {paramName} » en lecture seule";

            bool ok;
            switch (p.StorageType)
            {
                case StorageType.String:
                    ok = p.Set(text);
                    break;
                case StorageType.Integer:
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                        return $"« {text} » n'est pas un entier pour « {paramName} »";
                    ok = p.Set(i);
                    break;
                case StorageType.Double:
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                        return $"« {text} » n'est pas un nombre pour « {paramName} »";
                    ok = p.Set(d);
                    break;
                default:
                    return $"type de paramètre non pris en charge pour « {paramName} »";
            }
            return ok ? null : $"Revit a refusé la valeur de « {paramName} »";
        }

        /// <summary>Liste les paramètres Helora absents des pièces du projet.</summary>
        public static List<string> MissingParameters(Document doc, NumberingSettings s)
        {
            Room sample = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType().OfType<Room>().FirstOrDefault();
            if (sample == null) return new List<string>();
            return new[] { s.ParamBloc, s.ParamEtage, s.ParamGmao }
                .Where(n => !string.IsNullOrWhiteSpace(n) && sample.LookupParameter(n) == null).ToList();
        }
    }
}
