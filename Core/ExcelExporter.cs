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
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>Une feuille de données modifiable : des lignes (éléments) et des colonnes (paramètres).</summary>
    internal sealed class SheetSource
    {
        public string Name { get; set; }
        public string Origin { get; set; }      // « Catégorie » ou « Nomenclature »
        public List<Element> Rows { get; set; }
        public List<ParamDescriptor> Descriptors { get; set; }
        public bool ByType { get; set; }
    }

    public static class ExcelExporter
    {
        private const int MaxDropdownItems = 3000;

        public static ExportSummary Export(Document doc, ExportRequest req)
        {
            var summary = new ExportSummary();

            using (var wb = new XLWorkbook())
            {
                wb.Style.Font.FontName = "Calibri";
                IXLWorksheet instructions = wb.AddWorksheet(InstructionsSheet);
                IXLWorksheet meta = wb.AddWorksheet(MetaSheet);
                meta.Visibility = XLWorksheetVisibility.VeryHidden;
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { InstructionsSheet, MetaSheet, ListsSheet };

                if (req.Kind == ExportKind.Schedules && req.ScheduleFormatted)
                {
                    // Mise en forme de la nomenclature : fidèle à Revit, mais non réimportable
                    meta.Cell(1, 1).Value = FormattedMarker;
                    foreach (ElementId id in req.ScheduleIds)
                    {
                        if (!(doc.GetElement(id) is ViewSchedule schedule)) continue;
                        IXLWorksheet ws = wb.AddWorksheet(UniqueSheetName(schedule.Name, usedNames));
                        ws.SetTabColor(Brand.Turquoise);
                        summary.Rows += ScheduleScanner.WriteFormatted(ws, schedule);
                        summary.Sheets++;
                    }
                    WriteInstructions(instructions, doc, req, summary.Notes);
                }
                else
                {
                    List<SheetSource> sources = req.Kind == ExportKind.Schedules
                        ? BuildScheduleSources(doc, req, summary)
                        : BuildCategorySources(doc, req, summary);

                    WriteDataWorkbook(doc, wb, meta, sources, usedNames, summary);
                    WriteInstructions(instructions, doc, req, summary.Notes);
                }

                instructions.SetTabActive();
                if (summary.Sheets == 0)
                    throw new InvalidOperationException("Aucune donnée à exporter pour la sélection choisie."
                        + (summary.Notes.Count > 0 ? "\n\n" + string.Join("\n", summary.Notes) : ""));

                wb.SaveAs(req.FilePath);
            }
            return summary;
        }

        // ------------------------------------------------------------------ sources

        private static List<SheetSource> BuildCategorySources(Document doc, ExportRequest req, ExportSummary summary)
        {
            List<ParamDescriptor> descriptors = req.ByType
                ? req.Parameters.Where(p => p.Owner == ParamOwner.Type).ToList()
                : req.Parameters.ToList();

            var list = new List<SheetSource>();
            foreach (CategoryData cat in req.Categories)
            {
                List<Element> rows = GetCategoryRows(doc, cat, req.ByType);
                if (rows.Count == 0 || descriptors.Count == 0) { summary.SkippedCategories.Add(cat.Name); continue; }
                list.Add(new SheetSource { Name = cat.Name, Origin = cat.Name, Rows = rows, Descriptors = descriptors, ByType = req.ByType });
            }
            return list;
        }

        private static List<SheetSource> BuildScheduleSources(Document doc, ExportRequest req, ExportSummary summary)
        {
            var list = new List<SheetSource>();
            foreach (ElementId id in req.ScheduleIds)
            {
                if (!(doc.GetElement(id) is ViewSchedule schedule)) continue;
                ScheduleContent content = ScheduleScanner.BuildEditable(doc, schedule);

                if (content.Skipped.Count > 0)
                    summary.Notes.Add($"« {schedule.Name} » : colonnes calculées non exportées ({string.Join(", ", content.Skipped)}).");

                if (content.Rows.Count == 0 || content.Descriptors.Count == 0)
                {
                    summary.SkippedCategories.Add(schedule.Name);
                    continue;
                }
                list.Add(new SheetSource
                {
                    Name = schedule.Name, Origin = "Nomenclature : " + schedule.Name,
                    Rows = content.Rows, Descriptors = content.Descriptors, ByType = false
                });
            }
            return list;
        }

        private static List<Element> GetCategoryRows(Document doc, CategoryData cat, bool byType)
        {
            var elements = cat.ElementIds.Select(id => doc.GetElement(id)).Where(e => e != null);
            if (!byType) return elements.ToList();

            return elements
                .Select(e => e.GetTypeId())
                .Where(id => id != ElementId.InvalidElementId)
                .Distinct()
                .Select(id => doc.GetElement(id))
                .OfType<ElementType>()
                .OrderBy(t => t.FamilyName).ThenBy(t => t.Name)
                .Cast<Element>()
                .ToList();
        }

        // ------------------------------------------------------------------ feuilles modifiables

        private static void WriteDataWorkbook(Document doc, XLWorkbook wb, IXLWorksheet meta, List<SheetSource> sources,
                                              HashSet<string> usedNames, ExportSummary summary)
        {
            var lookup = new ElementNameLookup(doc);
            IXLWorksheet lists = wb.AddWorksheet(ListsSheet);
            lists.Visibility = XLWorksheetVisibility.VeryHidden;
            var listRanges = new Dictionary<string, IXLRange>();
            int listCol = 1;

            string[] metaHeaders = { "Feuille", "Instantané", "Origine", "Mode", "Document", "Date" };
            for (int i = 0; i < metaHeaders.Length; i++) meta.Cell(1, i + 1).Value = metaHeaders[i];
            int metaRow = 2;
            int sheetIndex = 0;

            foreach (SheetSource src in sources)
            {
                sheetIndex++;
                string sheetName = UniqueSheetName(src.Name, usedNames);
                string snapName = SnapshotPrefix + sheetIndex;
                usedNames.Add(snapName);

                IXLWorksheet ws = wb.AddWorksheet(sheetName);
                IXLWorksheet snap = wb.AddWorksheet(snapName);
                snap.Visibility = XLWorksheetVisibility.VeryHidden;
                ws.SetTabColor(Brand.Turquoise);

                // --- Colonnes fixes
                WriteFixedHeader(ws, snap, ColUid, KeyUid, "UniqueId");
                WriteFixedHeader(ws, snap, ColTypeUid, KeyTypeUid, "UniqueId du type");
                WriteFixedHeader(ws, snap, ColElementId, KeyElementId, src.ByType ? "Id du type" : "Id");
                WriteFixedHeader(ws, snap, ColLabel, KeyLabel, "Famille : Type");

                // --- Colonnes de paramètres
                var cols = new List<ColumnSpec>();
                for (int j = 0; j < src.Descriptors.Count; j++)
                {
                    ParamDescriptor d = src.Descriptors[j];
                    ColumnSpec col = ColumnSpec.Create(doc, d, out string header);
                    if (d.Storage == StorageType.ElementId)
                        col.RefClass = DetectReferencedClass(doc, src.Rows, src.ByType, col);
                    cols.Add(col);

                    int c = FirstParamCol + j;
                    ws.Cell(KeyRow, c).Value = col.Serialize();
                    snap.Cell(KeyRow, c).Value = col.Serialize();

                    IXLCell h = ws.Cell(HeaderRow, c);
                    h.Value = header;
                    if (d.IsReadOnly)
                    {
                        h.Style.Fill.BackgroundColor = HeaderReadOnly;
                        h.Style.Font.FontColor = HeaderReadOnlyText;
                    }
                    else if (d.Owner == ParamOwner.Type)
                    {
                        h.Style.Fill.BackgroundColor = HeaderType;
                        h.Style.Font.FontColor = Brand.DarkGreen;
                    }
                    else if (Owners.IsRelated(d.Owner))
                    {
                        h.Style.Fill.BackgroundColor = HeaderRelated;
                        h.Style.Font.FontColor = Brand.DarkGreen;
                    }
                    else
                    {
                        h.Style.Fill.BackgroundColor = HeaderInstance;
                        h.Style.Font.FontColor = XLColor.White;
                    }
                    h.CreateComment().AddText(d.Origin + (d.IsReadOnly ? " – lecture seule" : "")
                        + (Owners.IsRelated(d.Owner) ? "\nUne modification s'applique à " + Owners.SharedNote(d.Owner) + "." : ""));
                }

                // --- Données
                int r = FirstDataRow;
                foreach (Element e in src.Rows)
                {
                    ElementType type = src.ByType ? e as ElementType : doc.GetElement(e.GetTypeId()) as ElementType;

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

                        Element owner = Owners.Resolve(doc, e, col.Owner, col.PhaseId, src.ByType);
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

                        if (p.IsReadOnly)
                        {
                            cell.Style.Fill.BackgroundColor = CellReadOnly;
                            cell.Style.Font.FontColor = CellReadOnlyText;
                        }
                        else if (Owners.IsShared(col.Owner) && !(src.ByType && col.Owner == ParamOwner.Type))
                        {
                            cell.Style.Fill.BackgroundColor = CellTypeParam;
                        }
                    }
                    r++;
                }
                int lastRow = r - 1;
                int lastCol = FirstParamCol + cols.Count - 1;

                // --- Formats et listes déroulantes
                for (int j = 0; j < cols.Count; j++)
                {
                    ColumnSpec col = cols[j];
                    int c = FirstParamCol + j;
                    IXLRange data = ws.Range(FirstDataRow, c, lastRow, c);

                    if (col.IsTextColumn) data.Style.NumberFormat.Format = "@";
                    else if (col.Storage == StorageType.Double) data.Style.NumberFormat.Format = "0.######";

                    if (src.Descriptors[j].IsReadOnly) continue;

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
                IXLRange fixedData = ws.Range(FirstDataRow, ColUid, lastRow, ColLabel);
                fixedData.Style.Fill.BackgroundColor = CellFixed;
                fixedData.Style.Font.FontColor = Brand.DarkGreen;

                IXLRange headerRange = ws.Range(HeaderRow, 1, HeaderRow, lastCol);
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Alignment.WrapText = true;
                headerRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                headerRange.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
                headerRange.Style.Border.BottomBorderColor = Brand.DarkGreen;
                ws.Row(HeaderRow).Height = 34;

                IXLRange body = ws.Range(FirstDataRow, 1, lastRow, lastCol);
                body.Style.Border.InsideBorder = XLBorderStyleValues.Hair;
                body.Style.Border.InsideBorderColor = Brand.Turquoise20;
                body.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                body.Style.Border.OutsideBorderColor = Brand.Turquoise50;

                ws.Row(KeyRow).Hide();
                ws.Column(ColUid).Hide();
                ws.Column(ColTypeUid).Hide();
                ws.Range(HeaderRow, 1, lastRow, lastCol).SetAutoFilter();
                ws.SheetView.Freeze(HeaderRow, ColLabel);
                try { ws.Columns(ColElementId, lastCol).AdjustToContents(HeaderRow, Math.Min(lastRow, HeaderRow + 300)); }
                catch { /* police manquante : sans conséquence */ }
                for (int c = ColElementId; c <= lastCol; c++)
                {
                    IXLColumn column = ws.Column(c);
                    if (column.Width > 45) column.Width = 45;
                    if (column.Width < 12) column.Width = 12;
                }

                meta.Cell(metaRow, 1).Value = sheetName;
                meta.Cell(metaRow, 2).Value = snapName;
                meta.Cell(metaRow, 3).Value = src.Origin;
                meta.Cell(metaRow, 4).Value = src.ByType ? ModeType : ModeInstance;
                meta.Cell(metaRow, 5).Value = doc.Title;
                meta.Cell(metaRow, 6).Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                metaRow++;

                summary.Sheets++;
                summary.Rows += src.Rows.Count;
            }
        }

        private static string DetectReferencedClass(Document doc, List<Element> rows, bool byType, ColumnSpec col)
        {
            int checkedCount = 0;
            foreach (Element e in rows)
            {
                Element owner = Owners.Resolve(doc, e, col.Owner, col.PhaseId, byType);
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
            h.Style.Font.FontColor = XLColor.White;
        }

        internal static string UniqueSheetName(string name, HashSet<string> used)
        {
            var sb = new StringBuilder();
            foreach (char ch in name)
                sb.Append("[]:*?/\\".IndexOf(ch) >= 0 ? '_' : ch);
            string baseName = sb.ToString().Trim('\'', ' ');
            if (baseName.Length == 0) baseName = "Feuille";
            if (baseName.Length > 28) baseName = baseName.Substring(0, 28);

            string candidate = baseName;
            int i = 2;
            while (used.Contains(candidate) || candidate.StartsWith("_"))
                candidate = (baseName.Length > 25 ? baseName.Substring(0, 25) : baseName) + " (" + i++ + ")";
            used.Add(candidate);
            return candidate;
        }

        // ------------------------------------------------------------------ instructions

        private static void WriteInstructions(IXLWorksheet ws, Document doc, ExportRequest req, List<string> notes)
        {
            bool formatted = req.Kind == ExportKind.Schedules && req.ScheduleFormatted;
            ws.SetTabColor(Brand.DarkGreen);
            ws.ShowGridLines = false;
            ws.Column(1).Width = 3;
            ws.Column(2).Width = 26;
            ws.Column(3).Width = 105;

            // Bandeau
            IXLRange band = ws.Range(1, 1, 3, 3);
            band.Style.Fill.BackgroundColor = Brand.DarkGreen;
            ws.Cell(2, 2).Value = "+|  Excel Link";
            ws.Cell(2, 2).Style.Font.FontSize = 18;
            ws.Cell(2, 2).Style.Font.FontColor = XLColor.White;
            ws.Cell(2, 3).Value = formatted ? "NOMENCLATURES – MISE EN FORME" : "EXPORT REVIT → EXCEL";
            ws.Cell(2, 3).Style.Font.FontColor = Brand.Turquoise50;
            ws.Cell(2, 3).Style.Font.Bold = true;
            ws.Cell(2, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Row(2).Height = 30;
            ws.Range(2, 2, 2, 3).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            int r = 5;
            void Title(string text)
            {
                ws.Cell(r, 2).Value = text.ToUpperInvariant();
                ws.Cell(r, 2).Style.Font.Bold = true;
                ws.Cell(r, 2).Style.Font.FontColor = Brand.Turquoise;
                ws.Range(r, 2, r, 3).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
                ws.Range(r, 2, r, 3).Style.Border.BottomBorderColor = Brand.Turquoise50;
                r += 2;
            }
            void Line(string a, string b)
            {
                ws.Cell(r, 2).Value = a;
                ws.Cell(r, 2).Style.Font.Bold = true;
                ws.Cell(r, 2).Style.Font.FontColor = Brand.DarkGreen;
                ws.Cell(r, 3).Value = b;
                ws.Cell(r, 3).Style.Alignment.WrapText = true;
                r++;
            }

            Title("Informations");
            Line("Projet", doc.Title);
            Line("Date", DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
            Line("Contenu", req.Kind == ExportKind.Schedules
                ? (formatted ? "Nomenclatures avec leur mise en forme Revit" : "Nomenclatures, une ligne par élément")
                : (req.ByType ? "Catégories, une ligne par type" : "Catégories, une ligne par élément"));
            r++;

            if (formatted)
            {
                Title("Important");
                Line("Réimport", "Ce fichier reproduit l'affichage des nomenclatures (titres, regroupements, totaux). "
                                + "Il n'est PAS réimportable dans Revit. Pour modifier des valeurs, exportez en mode « Modifiable ».");
                ws.Cell(r - 1, 3).Style.Font.FontColor = Brand.Orange;
            }
            else
            {
                Title("Légende des couleurs");
                void Legend(XLColor fill, XLColor font, string label, string text)
                {
                    IXLCell c = ws.Cell(r, 2);
                    c.Value = label;
                    c.Style.Fill.BackgroundColor = fill;
                    c.Style.Font.FontColor = font;
                    c.Style.Font.Bold = true;
                    c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Cell(r, 3).Value = text;
                    ws.Cell(r, 3).Style.Alignment.WrapText = true;
                    r++;
                }
                Legend(HeaderInstance, XLColor.White, "Occurrence", "Paramètre d'occurrence, modifiable.");
                Legend(HeaderType, Brand.DarkGreen, "Type", "Paramètre de type : la modification s'applique à toutes les occurrences du type (cellules turquoise clair).");
                Legend(HeaderRelated, Brand.DarkGreen, "Élément lié", "Paramètre d'un élément lié (pièce de départ / d'arrivée, pièce, espace, projet) : la modification s'applique à cet élément, donc à toutes ses lignes.");
                Legend(HeaderReadOnly, HeaderReadOnlyText, "Lecture seule", "Valeur non modifiable : les changements seront ignorés à l'import.");
                Legend(CellAbsent, Brand.DarkGreen, "Absent", "Ce paramètre n'existe pas pour cet élément.");
                Legend(HeaderFixed, XLColor.White, "Identification", "Colonnes d'identification : ne pas modifier.");
                r++;

                Title("Règles");
                string[] rules =
                {
                    "Vous pouvez trier, filtrer, déplacer les colonnes et utiliser des formules.",
                    "Ne renommez pas les feuilles et ne touchez pas à la ligne et aux colonnes masquées.",
                    "Les nombres sont dans l'unité indiquée entre crochets dans l'en-tête (ex. [mm]).",
                    "Oui / Non et les éléments liés (niveau, phase, matériau…) se choisissent dans la liste déroulante.",
                    "Seules les cellules modifiées sont réimportées. Ajouter ou supprimer des lignes n'a aucun effet dans Revit.",
                    "Pour un paramètre de type ou d'élément lié, la valeur est partagée : si vous la modifiez sur plusieurs lignes, saisissez partout la même.",
                    "Enregistrez le fichier avant de l'importer dans Revit.",
                };
                foreach (string rule in rules) Line("•", rule);
            }

            if (notes.Count > 0)
            {
                r++;
                Title("Remarques");
                foreach (string n in notes) Line("•", n);
            }
        }
    }
}
