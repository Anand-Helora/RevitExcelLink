using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

namespace RevitExcelLink.Core
{
    /// <summary>Valeur telle qu'écrite ou lue dans une cellule Excel.</summary>
    public readonly struct CellValue
    {
        public string Text { get; }
        public double? Number { get; }

        private CellValue(string text, double? number) { Text = text; Number = number; }

        public static CellValue FromText(string t) => new CellValue(t ?? "", null);
        public static CellValue FromNumber(double n) => new CellValue(null, n);
        public static readonly CellValue Empty = new CellValue("", null);

        public bool IsEmpty => Number == null && string.IsNullOrEmpty(Text);
    }

    /// <summary>
    /// Description complète d'une colonne de paramètre. Elle est sérialisée dans la ligne 1 (masquée)
    /// de chaque feuille, ce qui permet de relire le fichier même si les unités du projet ont changé.
    /// </summary>
    public sealed class ColumnSpec
    {
        public ParamOwner Owner { get; set; }
        public string Key { get; set; }
        public StorageType Storage { get; set; }
        public ForgeTypeId Spec { get; set; }      // type de données (longueur, surface…)
        public ForgeTypeId Unit { get; set; }      // unité d'affichage utilisée dans Excel (null = pas de conversion)
        public string RefClass { get; set; }       // pour les paramètres ElementId : classe des éléments référencés
        public string Name { get; set; }           // nom lisible (ligne d'en-tête)

        public bool IsYesNo => Storage == StorageType.Integer && Spec != null
                               && (Spec.TypeId ?? "").StartsWith("autodesk.spec:spec.bool", StringComparison.OrdinalIgnoreCase);

        public bool IsTextColumn => Storage == StorageType.String || Storage == StorageType.ElementId || IsYesNo;

        public string ColumnKey => (Owner == ParamOwner.Type ? "T|" : "I|") + Key;

        public string Serialize() => string.Join("|",
            Owner == ParamOwner.Type ? "T" : "I",
            Key,
            ((int)Storage).ToString(CultureInfo.InvariantCulture),
            Spec?.TypeId ?? "",
            Unit?.TypeId ?? "",
            RefClass ?? "");

        public static ColumnSpec TryParse(string s, string header)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string[] f = s.Split('|');
            if (f.Length < 6 || (f[0] != "I" && f[0] != "T")) return null;
            if (!int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int storage)) return null;
            return new ColumnSpec
            {
                Owner = f[0] == "T" ? ParamOwner.Type : ParamOwner.Instance,
                Key = f[1],
                Storage = (StorageType)storage,
                Spec = string.IsNullOrEmpty(f[3]) ? null : new ForgeTypeId(f[3]),
                Unit = string.IsNullOrEmpty(f[4]) ? null : new ForgeTypeId(f[4]),
                RefClass = string.IsNullOrEmpty(f[5]) ? null : f[5],
                Name = header
            };
        }

        /// <summary>Prépare la colonne pour l'export : unité d'affichage du projet et en-tête.</summary>
        public static ColumnSpec Create(Document doc, ParamDescriptor d, out string header)
        {
            var spec = new ColumnSpec
            {
                Owner = d.Owner,
                Key = d.Key,
                Storage = d.Storage,
                Spec = d.DataType,
                Name = d.Name
            };

            string unitLabel = null;
            if (d.Storage == StorageType.Double && IsMeasurable(d.DataType))
            {
                try
                {
                    FormatOptions fo = doc.GetUnits().GetFormatOptions(d.DataType);
                    spec.Unit = fo.GetUnitTypeId();
                    string unitId = spec.Unit.TypeId ?? "";
                    bool neutral = unitId.StartsWith("autodesk.unit.unit:general", StringComparison.OrdinalIgnoreCase)
                                   || unitId.StartsWith("autodesk.unit.unit:fixed", StringComparison.OrdinalIgnoreCase);
                    if (!neutral)
                    {
                        ForgeTypeId sym = fo.GetSymbolTypeId();
                        unitLabel = (sym != null && !sym.Empty())
                            ? LabelUtils.GetLabelForSymbol(sym)
                            : LabelUtils.GetLabelForUnit(spec.Unit);
                    }
                }
                catch
                {
                    spec.Unit = null; // on exporte alors la valeur interne brute
                }
            }

            header = d.Name
                     + (string.IsNullOrWhiteSpace(unitLabel) ? "" : " [" + unitLabel.Trim() + "]")
                     + (d.Owner == ParamOwner.Type ? " (type)" : "");
            return spec;
        }

        private static bool IsMeasurable(ForgeTypeId spec)
        {
            try { return spec != null && !spec.Empty() && UnitUtils.IsMeasurableSpec(spec); }
            catch { return false; }
        }
    }

    /// <summary>Résultat de la lecture d'une valeur saisie dans Excel.</summary>
    public sealed class ParseResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public object Value { get; set; }       // valeur prête pour Parameter.Set (unités internes)
        public string Canonical { get; set; }   // forme normalisée, comparable à ValueCodec.Normalize
    }

    public static class ValueCodec
    {
        public const string Yes = "Oui";
        public const string No = "Non";
        public const string None = "<Aucun>";
        public const string NotApplicable = "∅NA"; // marqueur « paramètre absent » dans l'instantané

        private const int Decimals = 6;

        /// <summary>Lit la valeur d'un paramètre Revit sous la forme écrite dans Excel.</summary>
        public static CellValue Read(Document doc, Parameter p, ColumnSpec col)
        {
            switch (p.StorageType)
            {
                case StorageType.Double:
                    if (!p.HasValue) return CellValue.Empty;
                    double d = p.AsDouble();
                    if (col.Unit != null) d = UnitUtils.ConvertFromInternalUnits(d, col.Unit);
                    return CellValue.FromNumber(Round(d));

                case StorageType.Integer:
                    if (!p.HasValue && !col.IsYesNo) return CellValue.Empty;
                    int i = p.AsInteger();
                    return col.IsYesNo ? CellValue.FromText(i != 0 ? Yes : No) : CellValue.FromNumber(i);

                case StorageType.String:
                    return CellValue.FromText(p.AsString() ?? "");

                case StorageType.ElementId:
                    ElementId id = p.AsElementId();
                    if (id == null || id == ElementId.InvalidElementId) return CellValue.FromText("");
                    Element el = doc.GetElement(id);
                    return CellValue.FromText(el?.Name ?? id.Value.ToString(CultureInfo.InvariantCulture));

                default:
                    return CellValue.Empty;
            }
        }

        /// <summary>Forme textuelle stable, utilisée pour comparer Excel / instantané / Revit.</summary>
        public static string Normalize(CellValue v)
        {
            if (v.Number.HasValue) return FormatNumber(v.Number.Value);
            return (v.Text ?? "").Replace("\r\n", "\n");
        }

        public static string FormatNumber(double n)
        {
            string s = Round(n).ToString("0.######", CultureInfo.InvariantCulture);
            return s == "-0" ? "0" : s;
        }

        private static double Round(double d) => Math.Round(d, Decimals, MidpointRounding.AwayFromZero);

        /// <summary>Convertit une saisie Excel en valeur Revit. <paramref name="lookup"/> résout les noms d'éléments.</summary>
        public static ParseResult Parse(Document doc, CellValue v, ColumnSpec col, ElementNameLookup lookup)
        {
            switch (col.Storage)
            {
                case StorageType.Double: return ParseDouble(doc, v, col);
                case StorageType.Integer: return col.IsYesNo ? ParseYesNo(v) : ParseInteger(v);
                case StorageType.String:
                    string s = v.Number.HasValue ? FormatNumber(v.Number.Value) : (v.Text ?? "");
                    s = s.Replace("\r\n", "\n");
                    return new ParseResult { Ok = true, Value = s, Canonical = s };
                case StorageType.ElementId: return ParseElementId(v, col, lookup);
                default: return Fail("Type de paramètre non pris en charge.");
            }
        }

        private static ParseResult ParseDouble(Document doc, CellValue v, ColumnSpec col)
        {
            if (v.IsEmpty) return Fail("Valeur vide : Revit ne permet pas d'effacer une valeur numérique.");

            double display;
            if (v.Number.HasValue)
            {
                display = v.Number.Value;
            }
            else if (!TryParseLooseNumber(v.Text, out display))
            {
                // Dernier recours : laisser Revit interpréter la saisie (ex. 1' 6" ou 2.5 m)
                if (col.Spec != null && UnitFormatUtils.TryParse(doc.GetUnits(), col.Spec, v.Text, out double internalValue))
                {
                    display = col.Unit != null ? UnitUtils.ConvertFromInternalUnits(internalValue, col.Unit) : internalValue;
                }
                else
                {
                    return Fail($"« {v.Text} » n'est pas un nombre valide.");
                }
            }

            double value = col.Unit != null ? UnitUtils.ConvertToInternalUnits(display, col.Unit) : display;
            return new ParseResult { Ok = true, Value = value, Canonical = FormatNumber(display) };
        }

        private static ParseResult ParseInteger(CellValue v)
        {
            if (v.IsEmpty) return Fail("Valeur vide : Revit ne permet pas d'effacer une valeur entière.");
            double n;
            if (v.Number.HasValue) n = v.Number.Value;
            else if (!TryParseLooseNumber(v.Text, out n)) return Fail($"« {v.Text} » n'est pas un nombre entier.");

            if (Math.Abs(n - Math.Round(n)) > 1e-9 || n > int.MaxValue || n < int.MinValue)
                return Fail($"« {FormatNumber(n)} » n'est pas un nombre entier.");

            int i = (int)Math.Round(n);
            return new ParseResult { Ok = true, Value = i, Canonical = FormatNumber(i) };
        }

        private static ParseResult ParseYesNo(CellValue v)
        {
            string t = v.Number.HasValue ? FormatNumber(v.Number.Value) : (v.Text ?? "").Trim().ToLowerInvariant();
            switch (t)
            {
                case "oui": case "yes": case "vrai": case "true": case "1": case "x":
                    return new ParseResult { Ok = true, Value = 1, Canonical = Yes };
                case "non": case "no": case "faux": case "false": case "0": case "":
                    return new ParseResult { Ok = true, Value = 0, Canonical = No };
                default:
                    return Fail($"« {t} » : utilisez Oui ou Non.");
            }
        }

        private static ParseResult ParseElementId(CellValue v, ColumnSpec col, ElementNameLookup lookup)
        {
            string name = v.Number.HasValue ? FormatNumber(v.Number.Value) : (v.Text ?? "").Trim();
            if (name.Length == 0 || name == None)
                return new ParseResult { Ok = true, Value = ElementId.InvalidElementId, Canonical = "" };

            if (string.IsNullOrEmpty(col.RefClass))
                return Fail("Impossible de déterminer le type d'élément attendu pour ce paramètre.");

            var matches = lookup.Find(col.RefClass, name);
            if (matches.Count == 0) return Fail($"Aucun élément nommé « {name} ».");
            if (matches.Count > 1) return Fail($"Nom ambigu : {matches.Count} éléments s'appellent « {name} ».");
            return new ParseResult { Ok = true, Value = matches[0], Canonical = name };
        }

        /// <summary>Accepte « 2500 », « 2 500,5 », « 2500 mm », « 12,5 % »…</summary>
        public static bool TryParseLooseNumber(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string t = text.Trim();
            var sb = new StringBuilder();
            int i = 0;
            for (; i < t.Length; i++)
            {
                char c = t[i];
                if (char.IsDigit(c) || ((c == '-' || c == '+') && sb.Length == 0)) sb.Append(c);
                else if (c == ',' || c == '.') sb.Append('.');
                else if (char.IsWhiteSpace(c) || c == ' ' || c == ' ') continue; // séparateurs de milliers
                else break; // début de l'unité
            }

            // S'il reste des chiffres après l'unité (ex. 1' 6"), on laisse Revit interpréter la saisie.
            for (int j = i; j < t.Length; j++)
                if (char.IsDigit(t[j])) return false;

            string s = sb.ToString();
            if (s.Length == 0 || s == "-" || s == "+") return false;
            // « 1.234.567 » : plusieurs points = séparateurs de milliers
            if (s.Count(ch => ch == '.') > 1)
            {
                int last = s.LastIndexOf('.');
                s = s.Substring(0, last).Replace(".", "") + s.Substring(last);
            }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static ParseResult Fail(string msg) => new ParseResult { Ok = false, Error = msg };
    }
}
