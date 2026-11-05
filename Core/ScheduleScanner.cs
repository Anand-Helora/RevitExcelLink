using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using ClosedXML.Excel;

namespace RevitExcelLink.Core
{
    public sealed class ScheduleInfo
    {
        public ElementId Id { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
    }

    /// <summary>Contenu modifiable d'une nomenclature : ses éléments et ses colonnes de paramètres.</summary>
    public sealed class ScheduleContent
    {
        public List<Element> Rows { get; } = new List<Element>();
        public List<ParamDescriptor> Descriptors { get; } = new List<ParamDescriptor>();
        public List<string> Skipped { get; } = new List<string>();   // colonnes calculées (formules, totaux…)
    }

    public static class ScheduleScanner
    {
        private const int SampleSize = 300;

        /// <summary>Toutes les nomenclatures exportables du projet.</summary>
        public static List<ScheduleInfo> List(Document doc)
        {
            var result = new List<ScheduleInfo>();
            foreach (ViewSchedule s in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
            {
                try
                {
                    if (s.IsTemplate || s.IsTitleblockRevisionSchedule || s.IsInternalKeynoteSchedule) continue;
                    result.Add(new ScheduleInfo { Id = s.Id, Name = s.Name, Kind = KindOf(doc, s) });
                }
                catch
                {
                    // nomenclature spéciale non lisible : ignorée
                }
            }
            return result.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static string KindOf(Document doc, ViewSchedule s)
        {
            ScheduleDefinition def = s.Definition;
            if (def.IsKeySchedule) return "Nomenclature de clés";
            if (def.IsMaterialTakeoff) return "Métré de matériaux";

            ElementId catId = def.CategoryId;
            if (catId == ElementId.InvalidElementId) return "Multicatégorie";
            long v = catId.Value;
            if (v == (long)BuiltInCategory.OST_Sheets) return "Liste de feuilles";
            if (v == (long)BuiltInCategory.OST_Views) return "Liste de vues";
            Category cat = Category.GetCategory(doc, catId);
            return cat != null ? cat.Name : "Nomenclature";
        }

        // ------------------------------------------------------------------ mode modifiable

        /// <summary>
        /// Lignes = éléments affichés par la nomenclature (filtres compris), dans l'ordre de tri de la nomenclature.
        /// Colonnes = champs visibles qui correspondent à un paramètre d'occurrence ou de type.
        /// </summary>
        public static ScheduleContent BuildEditable(Document doc, ViewSchedule schedule)
        {
            var content = new ScheduleContent();
            ScheduleDefinition def = schedule.Definition;

            List<Element> rows;
            try
            {
                rows = new FilteredElementCollector(doc, schedule.Id).ToElements().Where(e => e != null).ToList();
            }
            catch
            {
                rows = new List<Element>();
            }

            var fieldMap = new Dictionary<int, (string Key, ParamOwner Owner)>();
            var seen = new HashSet<string>();

            foreach (ScheduleFieldId fieldId in def.GetFieldOrder())
            {
                ScheduleField field = def.GetField(fieldId);
                string heading = Heading(field);

                if (!TryGetKey(doc, field, out string key, out ParamOwner owner))
                {
                    if (!field.IsHidden) content.Skipped.Add(heading);
                    continue;
                }
                fieldMap[fieldId.IntegerValue] = (key, owner);
                if (field.IsHidden) continue;

                string id = (owner == ParamOwner.Type ? "T|" : "I|") + key;
                if (!seen.Add(id)) continue;

                ParamDescriptor desc = Describe(doc, rows, key, owner, heading);
                if (desc == null) { content.Skipped.Add(heading); continue; }
                content.Descriptors.Add(desc);
            }

            content.Rows.AddRange(SortLikeSchedule(doc, def, rows, fieldMap));
            return content;
        }

        private static string Heading(ScheduleField field)
        {
            string h = null;
            try { h = field.ColumnHeading; } catch { }
            if (string.IsNullOrWhiteSpace(h)) { try { h = field.GetName(); } catch { } }
            return string.IsNullOrWhiteSpace(h) ? "(sans nom)" : h.Trim();
        }

        private static bool TryGetKey(Document doc, ScheduleField field, out string key, out ParamOwner owner)
        {
            key = null;
            owner = ParamOwner.Instance;

            switch (field.FieldType)
            {
                case ScheduleFieldType.Instance: owner = ParamOwner.Instance; break;
                case ScheduleFieldType.ElementType: owner = ParamOwner.Type; break;
                default: return false;   // formules, totaux, pourcentages, quantités de matériaux…
            }

            ElementId pid = field.ParameterId;
            if (pid == null || pid == ElementId.InvalidElementId) return false;

            if (pid.Value < 0)
            {
                key = "B:" + ((int)pid.Value).ToString(CultureInfo.InvariantCulture);
                return true;
            }

            Element pe = doc.GetElement(pid);
            if (pe is SharedParameterElement shared)
            {
                key = "S:" + shared.GuidValue.ToString("D");
                return true;
            }
            if (pe is ParameterElement param)
            {
                key = "N:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(param.GetDefinition().Name));
                return true;
            }
            return false;
        }

        private static ParamDescriptor Describe(Document doc, List<Element> rows, string key, ParamOwner owner, string heading)
        {
            ParamDescriptor desc = null;
            int n = 0;
            foreach (Element e in rows)
            {
                Element target = owner == ParamOwner.Type ? doc.GetElement(e.GetTypeId()) : e;
                Parameter p = ParamKeys.Find(target, key);
                if (p == null || p.StorageType == StorageType.None) continue;

                if (desc == null)
                {
                    desc = new ParamDescriptor
                    {
                        Key = key,
                        Name = heading,
                        Owner = owner,
                        IsReadOnly = p.IsReadOnly,
                        Storage = p.StorageType,
                        DataType = ParamKeys.SafeDataType(p),
                        Origin = ParamKeys.OriginOf(p) + " – " + p.Definition.Name
                    };
                }
                else
                {
                    desc.IsReadOnly = desc.IsReadOnly && p.IsReadOnly;
                }
                if (++n >= SampleSize) break;
            }
            return desc;
        }

        /// <summary>Reproduit l'ordre de tri/regroupement défini dans la nomenclature.</summary>
        private static IEnumerable<Element> SortLikeSchedule(Document doc, ScheduleDefinition def, List<Element> rows,
                                                            Dictionary<int, (string Key, ParamOwner Owner)> fieldMap)
        {
            var sorts = new List<(string Key, ParamOwner Owner, bool Descending)>();
            try
            {
                for (int i = 0; i < def.GetSortGroupFieldCount(); i++)
                {
                    ScheduleSortGroupField sg = def.GetSortGroupField(i);
                    if (fieldMap.TryGetValue(sg.FieldId.IntegerValue, out var f))
                        sorts.Add((f.Key, f.Owner, sg.SortOrder == ScheduleSortOrder.Descending));
                }
            }
            catch { }

            if (sorts.Count == 0) return rows;

            var keyed = rows.Select(e => new
            {
                Element = e,
                Keys = sorts.Select(s => SortValue(doc, e, s.Key, s.Owner)).ToArray()
            }).ToList();

            keyed.Sort((a, b) =>
            {
                for (int i = 0; i < sorts.Count; i++)
                {
                    int c = CompareValues(a.Keys[i], b.Keys[i]);
                    if (c != 0) return sorts[i].Descending ? -c : c;
                }
                return a.Element.Id.Value.CompareTo(b.Element.Id.Value);
            });
            return keyed.Select(k => k.Element);
        }

        private static object SortValue(Document doc, Element e, string key, ParamOwner owner)
        {
            Element target = owner == ParamOwner.Type ? doc.GetElement(e.GetTypeId()) : e;
            Parameter p = ParamKeys.Find(target, key);
            if (p == null || !p.HasValue) return null;
            switch (p.StorageType)
            {
                case StorageType.Double: return p.AsDouble();
                case StorageType.Integer: return (double)p.AsInteger();
                case StorageType.String: return p.AsString() ?? "";
                case StorageType.ElementId: return p.AsValueString() ?? "";
                default: return null;
            }
        }

        private static int CompareValues(object a, object b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            if (a is double da && b is double db) return da.CompareTo(db);
            return NaturalCompare(a.ToString(), b.ToString());
        }

        /// <summary>Tri « naturel » : P2 avant P10, comme Revit.</summary>
        private static int NaturalCompare(string x, string y)
        {
            var rx = new Regex(@"\d+|\D+");
            var px = rx.Matches(x ?? "");
            var py = rx.Matches(y ?? "");
            for (int i = 0; i < Math.Min(px.Count, py.Count); i++)
            {
                string sx = px[i].Value, sy = py[i].Value;
                int c;
                if (char.IsDigit(sx[0]) && char.IsDigit(sy[0])
                    && decimal.TryParse(sx, out decimal nx) && decimal.TryParse(sy, out decimal ny))
                    c = nx.CompareTo(ny);
                else
                    c = string.Compare(sx, sy, StringComparison.CurrentCultureIgnoreCase);
                if (c != 0) return c;
            }
            return px.Count.CompareTo(py.Count);
        }

        // ------------------------------------------------------------------ mode mise en forme

        private static readonly Regex PlainNumber = new Regex(@"^-?\d+([.,]\d+)?$");

        /// <summary>Recopie la nomenclature telle qu'affichée (en-tête, titres de colonnes, regroupements, totaux).</summary>
        public static int WriteFormatted(IXLWorksheet ws, ViewSchedule schedule)
        {
            TableData table = schedule.GetTableData();
            int excelRow = 1;
            int maxCol = 1;
            int bodyRows = 0;

            foreach (SectionType st in new[] { SectionType.Header, SectionType.Body })
            {
                TableSectionData sd;
                try { sd = table.GetSectionData(st); } catch { sd = null; }
                if (sd == null || sd.NumberOfRows == 0 || sd.NumberOfColumns == 0) continue;

                int firstRow = sd.FirstRowNumber, firstCol = sd.FirstColumnNumber;
                int startExcelRow = excelRow;

                for (int r = firstRow; r < firstRow + sd.NumberOfRows; r++)
                {
                    for (int c = firstCol; c < firstCol + sd.NumberOfColumns; c++)
                    {
                        int xr = excelRow + (r - firstRow);
                        int xc = 1 + (c - firstCol);
                        maxCol = Math.Max(maxCol, xc);
                        IXLCell cell = ws.Cell(xr, xc);

                        string text = "";
                        try { text = schedule.GetCellText(st, r, c) ?? ""; } catch { }
                        if (PlainNumber.IsMatch(text)
                            && double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                            cell.Value = n;
                        else if (text.Length > 0)
                            cell.Value = text;

                        ApplyStyle(cell, sd, r, c);

                        try
                        {
                            TableMergedCell m = sd.GetMergedCell(r, c);
                            if (m.Left == c && m.Top == r && (m.Right > m.Left || m.Bottom > m.Top))
                                ws.Range(xr, xc, excelRow + (m.Bottom - firstRow), 1 + (m.Right - firstCol)).Merge();
                        }
                        catch { }
                    }
                }

                // largeurs de colonnes (pieds -> largeur Excel approximative)
                for (int c = firstCol; c < firstCol + sd.NumberOfColumns; c++)
                {
                    try
                    {
                        double mm = sd.GetColumnWidth(c) * 304.8;
                        double width = Math.Max(6, Math.Min(80, mm / 1.9));
                        IXLColumn col = ws.Column(1 + (c - firstCol));
                        if (st == SectionType.Body || col.Width < width) col.Width = width;
                    }
                    catch { }
                }

                IXLRange range = ws.Range(startExcelRow, 1, excelRow + sd.NumberOfRows - 1, sd.NumberOfColumns);
                range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                range.Style.Border.OutsideBorderColor = Brand.DarkGreen;
                range.Style.Border.InsideBorderColor = Brand.Turquoise50;
                range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

                if (st == SectionType.Header)
                {
                    range.Style.Fill.BackgroundColor = Brand.DarkGreen;
                    range.Style.Font.FontColor = XLColor.White;
                    range.Style.Font.Bold = true;
                    range.Style.Font.FontSize = 13;
                }
                else
                {
                    bodyRows = sd.NumberOfRows;
                }
                excelRow += sd.NumberOfRows;
            }

            if (excelRow > 2) ws.SheetView.FreezeRows(Math.Min(excelRow - 1, 3));
            return bodyRows;
        }

        private static void ApplyStyle(IXLCell cell, TableSectionData sd, int r, int c)
        {
            try
            {
                TableCellStyle style = sd.GetTableCellStyle(r, c);
                if (style.IsFontBold) cell.Style.Font.Bold = true;
                if (style.IsFontItalic) cell.Style.Font.Italic = true;
                if (style.IsFontUnderline) cell.Style.Font.Underline = XLFontUnderlineValues.Single;

                switch (style.FontHorizontalAlignment)
                {
                    case HorizontalAlignmentStyle.Center: cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center; break;
                    case HorizontalAlignmentStyle.Right: cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right; break;
                    default: cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left; break;
                }

                Color bg = style.BackgroundColor;
                if (bg != null && bg.IsValid && !(bg.Red == 255 && bg.Green == 255 && bg.Blue == 255))
                    cell.Style.Fill.BackgroundColor = XLColor.FromArgb(bg.Red, bg.Green, bg.Blue);

                Color fg = style.TextColor;
                if (fg != null && fg.IsValid && !(fg.Red == 0 && fg.Green == 0 && fg.Blue == 0))
                    cell.Style.Font.FontColor = XLColor.FromArgb(fg.Red, fg.Green, fg.Blue);
            }
            catch
            {
                // style non disponible pour cette cellule
            }
        }
    }
}
