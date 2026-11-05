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

        /// <summary>Marqueur d'un classeur « mise en forme » (non réimportable).</summary>
        public const string FormattedMarker = "NON_REIMPORTABLE";

        // Couleurs Helora (même logique que la légende de l'onglet Instructions)
        public static readonly XLColor HeaderInstance = Brand.Turquoise;
        public static readonly XLColor HeaderType = Brand.Turquoise50;
        public static readonly XLColor HeaderRelated = Brand.Turquoise20;
        public static readonly XLColor HeaderReadOnly = XLColor.FromHtml("#E6ECED");
        public static readonly XLColor HeaderReadOnlyText = Brand.Orange;   // petite touche d'orange = signal
        public static readonly XLColor HeaderFixed = Brand.DarkGreen;

        public static readonly XLColor CellTypeParam = Brand.Turquoise10;
        public static readonly XLColor CellReadOnly = XLColor.FromHtml("#F3F5F5");
        public static readonly XLColor CellReadOnlyText = XLColor.FromHtml("#7A8B8E");
        public static readonly XLColor CellAbsent = XLColor.FromHtml("#C9D1D2");
        public static readonly XLColor CellFixed = Brand.DarkGreen10;

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

    /// <summary>Palette de la charte graphique Helora.</summary>
    public static class Brand
    {
        public static readonly XLColor DarkGreen = XLColor.FromHtml("#044550");   // vert foncé – Pantone 3165 C
        public static readonly XLColor Turquoise = XLColor.FromHtml("#009E99");   // turquoise – Pantone 326 C
        public static readonly XLColor Turquoise50 = XLColor.FromHtml("#80CFCC");
        public static readonly XLColor Turquoise20 = XLColor.FromHtml("#CCECEB");
        public static readonly XLColor Turquoise10 = XLColor.FromHtml("#E6F5F5");
        public static readonly XLColor DarkGreen10 = XLColor.FromHtml("#E6ECEE");
        public static readonly XLColor Orange = XLColor.FromHtml("#E16021");      // orange – petites touches uniquement
    }
}
