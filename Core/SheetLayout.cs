using ClosedXML.Excel;

namespace RevitExcelLink.Core
{
    /// <summary>Organisation du classeur, partagée par l'export et l'import.</summary>
    public static class SheetLayout
    {
        public const int KeyRow = 1;        // ligne masquée : description technique des colonnes
        public const int HeaderRow = 2;     // en-têtes lisibles
        public const int FirstDataRow = 3;

        public const int ColUid = 1;        // UniqueId de l'élément (masquée)
        public const int ColTypeUid = 2;    // UniqueId du type (masquée)
        public const int ColElementId = 3;
        public const int ColLabel = 4;      // Famille : Type
        public const int FirstParamCol = 5;

        public const string KeyUid = "#UID";
        public const string KeyTypeUid = "#TUID";
        public const string KeyElementId = "#EID";
        public const string KeyLabel = "#NAME";

        public const string InstructionsSheet = "Instructions";
        public const string MetaSheet = "_meta";
        public const string ListsSheet = "_listes";
        public const string SnapshotPrefix = "_s";

        public const string ModeInstance = "Instance";
        public const string ModeType = "Type";

        // Couleurs (même logique que la légende de l'onglet Instructions)
        public static readonly XLColor HeaderInstance = XLColor.FromHtml("#C6EFCE");
        public static readonly XLColor HeaderType = XLColor.FromHtml("#FFEB9C");
        public static readonly XLColor HeaderReadOnly = XLColor.FromHtml("#FFC7CE");
        public static readonly XLColor HeaderFixed = XLColor.FromHtml("#D9D9D9");

        public static readonly XLColor CellTypeParam = XLColor.FromHtml("#FFF7D6");
        public static readonly XLColor CellReadOnly = XLColor.FromHtml("#FDE2E4");
        public static readonly XLColor CellAbsent = XLColor.FromHtml("#BFBFBF");
        public static readonly XLColor CellFixed = XLColor.FromHtml("#F2F2F2");

        /// <summary>Lit une cellule en tenant compte des formules (valeur calculée en cache).</summary>
        public static CellValue ReadCell(IXLCell cell)
        {
            XLCellValue v;
            try { v = cell.HasFormula ? cell.CachedValue : cell.Value; }
            catch { return CellValue.FromText(cell.GetFormattedString()); }

            switch (v.Type)
            {
                case XLDataType.Blank: return CellValue.Empty;
                case XLDataType.Number: return CellValue.FromNumber(v.GetNumber());
                case XLDataType.Text: return CellValue.FromText(v.GetText());
                case XLDataType.Boolean: return CellValue.FromText(v.GetBoolean() ? ValueCodec.Yes : ValueCodec.No);
                default: return CellValue.FromText(cell.GetFormattedString());
            }
        }

        public static string ReadText(IXLCell cell)
        {
            CellValue v = ReadCell(cell);
            return v.Number.HasValue ? ValueCodec.FormatNumber(v.Number.Value) : (v.Text ?? "").Trim();
        }
    }
}
