using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using ClosedXML.Excel;
using static RevitExcelLink.Core.SheetLayout;

namespace RevitExcelLink.Core
{
    public sealed class ExportSummary
    {
        public int Sheets { get; set; }
        public int Rows { get; set; }
        public List<string> SkippedCategories { get; } = new List<string>();
    }

    public static class ExcelExporter
    {
        private const int MaxDropdownItems = 3000;

        public static ExportSummary Export(Document doc, ExportRequest req)
        {
            var summary = new ExportSummary();
            var lookup = new ElementNameLookup(doc);

            using (var wb = new XLWorkbook())
            {
                IXLWorksheet instructions = wb.AddWorksheet(InstructionsSheet);
                IXLWorksheet meta = wb.AddWorksheet(MetaSheet);
                IXLWorksheet lists = wb.AddWorksheet(ListsSheet);
                meta.Visibility = XLWorksheetVisibility.VeryHidden;
                lists.Visibility = XLWorksheetVisibility.VeryHidden;

                meta.Cell(1, 1).Value = "Feuille";
                meta.Cell(1, 2).Value = "Instantané";
                meta.Cell(1, 3).Value = "Catégorie";
                meta.Cell(1, 4).Value = "Mode";
                meta.Cell(1, 5).Value = "Document";
                meta.Cell(1, 6).Value = "Date";
                int metaRow = 2;

                var listRanges = new Dictionary<string, IXLRange>();
                int listCol = 1;
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { InstructionsSheet, MetaSheet, ListsSheet };

                List<ParamDescriptor> descriptors = req.ByType
                    ? req.Parameters.Where(p => p.Owner == ParamOwner.Type).ToList()
                    : req.Parameters.ToList();

                int sheetIndex = 0;
                foreach (CategoryData cat in req.Categories)
                {
                    List<Element> rows = GetRows(doc, cat, req.ByType);
                    if (rows.Count == 0 || descriptors.Count == 0)
                    {
                        summary.SkippedCategories.Add(cat.Name);
                        continue;
                    }

                    sheetIndex++;
                    string sheetName = UniqueSheetName(cat.Name, usedNames);
                    string snapName = SnapshotPrefix + sheetIndex;
                    usedNames.Add(snapName);

                    IXLWorksheet ws = wb.AddWorksheet(sheetName);
                    IXLWorksheet snap = wb.AddWorksheet(snapName);
                    snap.Visibility = XLWorksheetVisibility.VeryHidden;

                    // --- Colonnes fixes
                    WriteFixedHeader(ws, snap, ColUid, KeyUid, "UniqueId");
                    WriteFixedHeader(ws, snap, ColTypeUid, KeyTypeUid, "UniqueId du type");
                    WriteFixedHeader(ws, snap, ColElementId, KeyElementId, req.ByType ? "Id du type" : "Id de l'élément");
                    WriteFixedHeader(ws, snap, ColLabel, KeyLabel, "Famille : Type");

                    // --- Colonnes de paramètres
                    var cols = new List<ColumnSpec>();
                    for (int j = 0; j < descriptors.Count; j++)
                    {
                        ParamDescriptor d = descriptors[j];
                        ColumnSpec col = ColumnSpec.Create(doc, d, out string header);
                        if (d.Storage == StorageType.ElementId)
                            col.RefClass = DetectReferencedClass(doc, rows, req.ByType, col);
                        cols.Add(col);

                        int c = FirstParamCol + j;
                        ws.Cell(KeyRow, c).Value = col.Serialize();
                        snap.Cell(KeyRow, c).Value = col.Serialize();
                        IXLCell h = ws.Cell(HeaderRow, c);
                        h.Value = header;
                        h.Style.Fill.BackgroundColor = d.IsReadOnly ? HeaderReadOnly
                            : d.Owner == ParamOwner.Type ? HeaderType : HeaderInstance;
                        h.CreateComment().AddText(d.Origin + (d.IsReadOnly ? " – lecture seule" : ""));
                    }

                    // --- Données
                    int r = FirstDataRow;
                    foreach (Element e in rows)
                    {
                        ElementType type = req.ByType ? e as ElementType : doc.GetElement(e.GetTypeId()) as ElementType;

                        ws.Cell(r, ColUid).Value = e.UniqueId;
                        ws.Cell(r, ColTypeUid).Value = type?.UniqueId ?? "";
                        ws.Cell(r, ColElementId).Value = (double)e.Id.Value;
                        ws.Cell(r, ColLabel).Value = Label(e, type);
                        snap.Cell(r, ColUid).Value = e.UniqueId;

                        for (int j = 0; j < cols.Count; j++)
                        {
                            ColumnSpec col = cols[j];
                            int c = FirstParamCol + j;
                            IXLCell cell = ws.Cell(r, c);

                            Element owner = col.Owner == ParamOwner.Type ? (Element)type : e;
                            Parameter p = ParamKeys.Find(owner, col.Key);
                            if (p == null)
                            {
                                cell.Style.Fill.BackgroundColor = CellAbsent;
                                snap.Cell(r, c).Value = ValueCodec.NotApplicable;
                                continue;
                            }

                            CellValue v = ValueCodec.Read(doc, p, col);
                            if (v.Number.HasValue) cell.Value = v.Number.Value;
                            else if (!string.IsNullOrEmpty(v.Text)) cell.Value = v.Text;
                            snap.Cell(r, c).Value = ValueCodec.Normalize(v);

                            if (p.IsReadOnly) cell.Style.Fill.BackgroundColor = CellReadOnly;
                            else if (col.Owner == ParamOwner.Type && !req.ByType) cell.Style.Fill.BackgroundColor = CellTypeParam;
                        }
                        r++;
                    }
                    int lastRow = r - 1;
                    int lastCol = FirstParamCol + cols.Count - 1;

                    // --- Formats et listes déroulantes par colonne
                    for (int j = 0; j < cols.Count; j++)
                    {
                        ColumnSpec col = cols[j];
                        int c = FirstParamCol + j;
                        IXLRange data = ws.Range(FirstDataRow, c, lastRow, c);

                        if (col.IsTextColumn) data.Style.NumberFormat.Format = "@"; // garde les zéros en tête
                        else if (col.Storage == StorageType.Double) data.Style.NumberFormat.Format = "0.######";

                        if (descriptors[j].IsReadOnly) continue;

                        List<string> values = null;
                        string listKey = null;
                        if (col.IsYesNo)
                        {
                            listKey = "YESNO";
                            values = new List<string> { ValueCodec.Yes, ValueCodec.No };
                        }
                        else if (col.Storage == StorageType.ElementId && col.RefClass != null)
                        {
                            listKey = col.RefClass;
                            values = lookup.Names(col.RefClass);
                            values.Insert(0, ValueCodec.None);
                        }

                        if (listKey != null && values.Count > 1 && values.Count <= MaxDropdownItems)
                        {
                            if (!listRanges.TryGetValue(listKey, out IXLRange listRange))
                            {
                                for (int k = 0; k < values.Count; k++) lists.Cell(k + 1, listCol).Value = values[k];
                                listRange = lists.Range(1, listCol, values.Count, listCol);
                                listRanges[listKey] = listRange;
                                listCol++;
                            }
                            IXLDataValidation dv = data.CreateDataValidation();
                            dv.List(listRange, true);
                            dv.IgnoreBlanks = true;
                            dv.ShowErrorMessage = true;
                            dv.ErrorStyle = XLErrorStyle.Stop;
                            dv.ErrorTitle = "Valeur non valide";
                            dv.ErrorMessage = "Choisissez une valeur dans la liste.";
                        }
                    }

                    // --- Mise en page
                    ws.Range(FirstDataRow, ColUid, lastRow, ColLabel).Style.Fill.BackgroundColor = CellFixed;
                    IXLRange headerRange = ws.Range(HeaderRow, 1, HeaderRow, lastCol);
                    headerRange.Style.Font.Bold = true;
                    headerRange.Style.Alignment.WrapText = true;
                    headerRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                    ws.Row(HeaderRow).Height = 32;
                    ws.Row(KeyRow).Hide();
                    ws.Column(ColUid).Hide();
                    ws.Column(ColTypeUid).Hide();
                    ws.Range(HeaderRow, 1, lastRow, lastCol).SetAutoFilter();
                    ws.SheetView.Freeze(HeaderRow, ColLabel);
                    try
                    {
                        ws.Columns(ColElementId, lastCol).AdjustToContents(HeaderRow, Math.Min(lastRow, HeaderRow + 300));
                    }
                    catch
                    {
                        // le calcul de largeur peut échouer si une police manque : sans conséquence
                    }
                    for (int c = ColElementId; c <= lastCol; c++)
                    {
                        IXLColumn column = ws.Column(c);
                        if (column.Width > 45) column.Width = 45;
                        if (column.Width < 12) column.Width = 12;
                    }

                    meta.Cell(metaRow, 1).Value = sheetName;
                    meta.Cell(metaRow, 2).Value = snapName;
                    meta.Cell(metaRow, 3).Value = cat.Name;
                    meta.Cell(metaRow, 4).Value = req.ByType ? ModeType : ModeInstance;
                    meta.Cell(metaRow, 5).Value = doc.Title;
                    meta.Cell(metaRow, 6).Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                    metaRow++;

                    summary.Sheets++;
                    summary.Rows += rows.Count;
                }

                WriteInstructions(instructions, doc, req);
                instructions.SetTabActive();
                if (summary.Sheets == 0)
                    throw new InvalidOperationException("Aucune donnée à exporter pour la sélection choisie.");

                wb.SaveAs(req.FilePath);
            }
            return summary;
        }

        private static List<Element> GetRows(Document doc, CategoryData cat, bool byType)
        {
            var elements = cat.ElementIds.Select(id => doc.GetElement(id)).Where(e => e != null);
            if (!byType) return elements.ToList();

            return elements
                .Select(e => e.GetTypeId())
                .Where(id => id != ElementId.InvalidElementId)
                .Distinct()
                .Select(id => doc.GetElement(id))
                .OfType<ElementType>()
                .Cast<Element>()
                .OrderBy(t => ((ElementType)t).FamilyName).ThenBy(t => t.Name)
                .ToList();
        }

        private static string DetectReferencedClass(Document doc, List<Element> rows, bool byType, ColumnSpec col)
        {
            int checkedCount = 0;
            foreach (Element e in rows)
            {
                Element owner = col.Owner == ParamOwner.Type && !byType ? doc.GetElement(e.GetTypeId()) : e;
                Parameter p = ParamKeys.Find(owner, col.Key);
                if (p == null) continue;
                ElementId id = p.AsElementId();
                if (id != null && id != ElementId.InvalidElementId)
                {
                    Element target = doc.GetElement(id);
                    if (target != null) return target.GetType().FullName;
                }
                if (++checkedCount > 2000) break;
            }
            return null;
        }

        private static string Label(Element e, ElementType type)
        {
            if (type != null && !string.IsNullOrEmpty(type.FamilyName)) return type.FamilyName + " : " + type.Name;
            return type?.Name ?? e.Name;
        }

        private static void WriteFixedHeader(IXLWorksheet ws, IXLWorksheet snap, int col, string key, string header)
        {
            ws.Cell(KeyRow, col).Value = key;
            snap.Cell(KeyRow, col).Value = key;
            IXLCell h = ws.Cell(HeaderRow, col);
            h.Value = header;
            h.Style.Fill.BackgroundColor = HeaderFixed;
        }

        private static string UniqueSheetName(string name, HashSet<string> used)
        {
            var sb = new StringBuilder();
            foreach (char ch in name)
                sb.Append("[]:*?/\\".IndexOf(ch) >= 0 ? '_' : ch);
            string baseName = sb.ToString().Trim('\'', ' ');
            if (baseName.Length == 0) baseName = "Catégorie";
            if (baseName.Length > 28) baseName = baseName.Substring(0, 28);

            string candidate = baseName;
            int i = 2;
            while (used.Contains(candidate) || candidate.StartsWith("_"))
                candidate = (baseName.Length > 25 ? baseName.Substring(0, 25) : baseName) + " (" + i++ + ")";
            used.Add(candidate);
            return candidate;
        }

        private static void WriteInstructions(IXLWorksheet ws, Document doc, ExportRequest req)
        {
            int r = 1;
            ws.Cell(r, 1).Value = "Export Revit → Excel";
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 1).Style.Font.FontSize = 16;
            r += 2;

            void Line(string a, string b)
            {
                ws.Cell(r, 1).Value = a;
                ws.Cell(r, 1).Style.Font.Bold = true;
                ws.Cell(r, 2).Value = b;
                r++;
            }

            Line("Projet", doc.Title);
            Line("Date", DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
            Line("Mode", req.ByType ? "Une ligne par type (paramètres de type uniquement)" : "Une ligne par élément");
            r++;

            ws.Cell(r++, 1).Value = "Légende des couleurs";
            ws.Cell(r - 1, 1).Style.Font.Bold = true;

            void Legend(XLColor color, string text)
            {
                ws.Cell(r, 1).Style.Fill.BackgroundColor = color;
                ws.Cell(r, 2).Value = text;
                r++;
            }

            Legend(HeaderInstance, "En-tête vert : paramètre d'occurrence (modifiable).");
            Legend(HeaderType, "En-tête / cellule jaune : paramètre de type. Une modification s'applique à toutes les occurrences du type.");
            Legend(HeaderReadOnly, "Rouge : lecture seule. Les modifications seront ignorées à l'import.");
            Legend(CellAbsent, "Gris foncé : ce paramètre n'existe pas pour cet élément.");
            Legend(CellFixed, "Gris clair : colonnes d'identification, ne pas modifier.");
            r++;

            ws.Cell(r++, 1).Value = "Règles";
            ws.Cell(r - 1, 1).Style.Font.Bold = true;
            string[] rules =
            {
                "Vous pouvez trier, filtrer, déplacer les colonnes et utiliser des formules.",
                "Ne renommez pas les feuilles et ne modifiez pas la ligne masquée n°1 ni les colonnes masquées.",
                "Les nombres sont dans l'unité indiquée entre crochets dans l'en-tête (ex. [mm]).",
                "Oui / Non et les éléments liés (niveau, phase, matériau…) se choisissent dans la liste déroulante.",
                "Seules les cellules que vous modifiez sont réimportées. Ajouter ou supprimer des lignes n'a aucun effet dans Revit.",
                "Pour un paramètre de type, saisissez la même valeur sur toutes les lignes du type que vous modifiez.",
                "Enregistrez le fichier avant de l'importer dans Revit.",
            };
            foreach (string rule in rules)
            {
                ws.Cell(r, 1).Value = "•";
                ws.Cell(r, 2).Value = rule;
                r++;
            }

            ws.Column(1).Width = 24;
            ws.Column(2).Width = 110;
            ws.Column(2).Style.Alignment.WrapText = true;
        }
    }
}
