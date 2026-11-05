using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using ClosedXML.Excel;
using static RevitExcelLink.Core.SheetLayout;

namespace RevitExcelLink.Core
{
    public sealed class ImportAnalysis
    {
        public List<ChangeItem> Items { get; } = new List<ChangeItem>();
        public List<string> Warnings { get; } = new List<string>();
        public int RowsRead { get; set; }
        public int SheetsRead { get; set; }
    }

    /// <summary>
    /// Lit un classeur produit par l'export et liste les modifications à appliquer.
    /// Une cellule n'est retenue que si elle diffère de l'instantané pris à l'export :
    /// on n'écrase donc jamais une valeur que l'utilisateur n'a pas touchée dans Excel.
    /// </summary>
    public static class ExcelImporter
    {
        private sealed class TypeEdit
        {
            public ChangeItem Item;
            public string Canonical;
        }

        public static ImportAnalysis Analyze(Document doc, string path)
        {
            var result = new ImportAnalysis();
            var lookup = new ElementNameLookup(doc);

            using (var wb = OpenShared(path))
            {
                if (!wb.Worksheets.TryGetWorksheet(MetaSheet, out IXLWorksheet meta))
                    throw new InvalidDataException("Ce fichier n'a pas été créé par l'export de cet add-in (feuille « _meta » absente).");

                int lastMetaRow = meta.LastRowUsed()?.RowNumber() ?? 1;
                for (int mr = 2; mr <= lastMetaRow; mr++)
                {
                    string sheetName = meta.Cell(mr, 1).GetString();
                    string snapName = meta.Cell(mr, 2).GetString();
                    string mode = meta.Cell(mr, 4).GetString();
                    string docTitle = meta.Cell(mr, 5).GetString();
                    if (string.IsNullOrEmpty(sheetName)) continue;

                    if (mr == 2 && !string.IsNullOrEmpty(docTitle) && !string.Equals(docTitle, doc.Title, StringComparison.OrdinalIgnoreCase))
                        result.Warnings.Add($"Le fichier a été exporté depuis « {docTitle} », le projet actif est « {doc.Title} ». Les éléments sont retrouvés par leur identifiant unique.");

                    if (!wb.Worksheets.TryGetWorksheet(sheetName, out IXLWorksheet ws))
                    {
                        result.Warnings.Add($"Feuille « {sheetName} » introuvable (renommée ou supprimée) : ignorée.");
                        continue;
                    }

                    wb.Worksheets.TryGetWorksheet(snapName, out IXLWorksheet snap);
                    if (snap == null)
                        result.Warnings.Add($"Feuille « {sheetName} » : instantané d'export absent, comparaison directe avec Revit.");

                    AnalyzeSheet(doc, ws, snap, mode == ModeType, lookup, result);
                    result.SheetsRead++;
                }
            }

            return result;
        }

        private static XLWorkbook OpenShared(string path)
        {
            // Permet de lire le fichier même s'il est encore ouvert dans Excel
            var ms = new MemoryStream();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                fs.CopyTo(ms);
            ms.Position = 0;
            return new XLWorkbook(ms);
        }

        private static void AnalyzeSheet(Document doc, IXLWorksheet ws, IXLWorksheet snap, bool byType,
                                         ElementNameLookup lookup, ImportAnalysis result)
        {
            string sheetName = ws.Name;

            // --- Colonnes (repérées par la clé technique, donc robustes au déplacement)
            int lastCol = ws.Row(KeyRow).LastCellUsed()?.Address.ColumnNumber ?? 0;
            int uidCol = -1;
            var cols = new List<(int Col, ColumnSpec Spec)>();
            for (int c = 1; c <= lastCol; c++)
            {
                string key = ws.Cell(KeyRow, c).GetString();
                if (key == KeyUid) { uidCol = c; continue; }
                if (key.StartsWith("#")) continue;
                ColumnSpec spec = ColumnSpec.TryParse(key, ws.Cell(HeaderRow, c).GetString());
                if (spec != null) cols.Add((c, spec));
            }
            if (uidCol < 0)
            {
                result.Warnings.Add($"Feuille « {sheetName} » : colonne d'identifiants introuvable, feuille ignorée.");
                return;
            }

            Dictionary<string, Dictionary<string, string>> snapshot = snap != null ? ReadSnapshot(snap) : null;
            var typeEdits = new Dictionary<string, List<TypeEdit>>();
            int lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
            int orphanRows = 0;

            for (int r = FirstDataRow; r <= lastRow; r++)
            {
                string uid = ReadText(ws.Cell(r, uidCol));
                if (string.IsNullOrEmpty(uid))
                {
                    if (cols.Any(col => !ReadCell(ws.Cell(r, col.Col)).IsEmpty)) orphanRows++;
                    continue;
                }
                result.RowsRead++;

                Element element = doc.GetElement(uid);
                Dictionary<string, string> snapRow = null;
                snapshot?.TryGetValue(uid, out snapRow);

                if (element == null)
                {
                    // Signalé seulement si l'utilisateur a modifié quelque chose sur cette ligne
                    bool edited = snapRow == null || cols.Any(col =>
                        snapRow.TryGetValue(col.Spec.ColumnKey, out string o) && o != ValueCodec.NotApplicable &&
                        ValueCodec.Normalize(ReadCell(ws.Cell(r, col.Col))) != o);
                    if (edited)
                        result.Items.Add(new ChangeItem
                        {
                            Sheet = sheetName, ElementIdText = ReadText(ws.Cell(r, ColElementId)),
                            ElementLabel = ReadText(ws.Cell(r, ColLabel)), ParamName = "—",
                            Status = ChangeStatus.Missing, Message = "Élément introuvable dans le projet (supprimé ?)."
                        });
                    continue;
                }

                ElementType type = byType ? element as ElementType : doc.GetElement(element.GetTypeId()) as ElementType;

                foreach (var (c, spec) in cols)
                {
                    CellValue xv = ReadCell(ws.Cell(r, c));
                    string raw = ValueCodec.Normalize(xv);

                    string orig = null;
                    bool hasOrig = snapRow != null && snapRow.TryGetValue(spec.ColumnKey, out orig);
                    if (hasOrig && raw == orig) continue;                       // cellule non modifiée
                    if (hasOrig && orig == ValueCodec.NotApplicable && raw.Length == 0) continue;

                    Element owner = spec.Owner == ParamOwner.Type && !byType ? type : element;
                    Parameter p = ParamKeys.Find(owner, spec.Key);

                    var item = new ChangeItem
                    {
                        Sheet = sheetName,
                        ElementIdText = (owner ?? element).Id.Value.ToString(CultureInfo.InvariantCulture),
                        ElementLabel = Label(owner ?? element),
                        ParamName = spec.Name,
                        NewValue = raw,
                        TargetUid = (owner ?? element).UniqueId,
                        ParamKey = spec.Key,
                        Storage = spec.Storage
                    };

                    if (p == null)
                    {
                        if (!hasOrig && raw.Length == 0) continue;
                        item.Status = ChangeStatus.Missing;
                        item.Message = "Ce paramètre n'existe pas pour cet élément.";
                        result.Items.Add(item);
                        continue;
                    }

                    string current = ValueCodec.Normalize(ValueCodec.Read(doc, p, spec));
                    item.OldValue = current;
                    ParseResult parsed = ValueCodec.Parse(doc, xv, spec, lookup);

                    // Simple différence de format (ex. 2500,0 au lieu de 2500) : rien à faire
                    if (parsed.Ok && hasOrig && parsed.Canonical == orig) continue;
                    if (parsed.Ok && parsed.Canonical == current) continue;
                    if (!parsed.Ok && !hasOrig && raw == current) continue;

                    if (parsed.Ok)
                    {
                        item.NewValue = parsed.Canonical;
                        item.Value = parsed.Value;
                    }

                    if (p.IsReadOnly)
                    {
                        item.Status = ChangeStatus.ReadOnly;
                        item.Message = "Paramètre en lecture seule : modification ignorée.";
                    }
                    else if (!parsed.Ok)
                    {
                        item.Status = ChangeStatus.Error;
                        item.Message = parsed.Error;
                    }
                    else if (hasOrig && current != orig)
                    {
                        item.Status = ChangeStatus.Conflict;
                        item.Message = $"Modifié aussi dans Revit depuis l'export (valeur exportée : « {orig} »). Cochez pour forcer la valeur Excel.";
                    }
                    else
                    {
                        item.Status = ChangeStatus.Change;
                        item.Apply = true;
                    }

                    if (spec.Owner == ParamOwner.Type && !byType)
                    {
                        string groupKey = item.TargetUid + "|" + spec.ColumnKey;
                        if (!typeEdits.TryGetValue(groupKey, out var list)) typeEdits[groupKey] = list = new List<TypeEdit>();
                        list.Add(new TypeEdit { Item = item, Canonical = parsed.Ok ? parsed.Canonical : raw });
                    }
                    else
                    {
                        result.Items.Add(item);
                    }
                }
            }

            // --- Paramètres de type modifiés sur des lignes d'occurrences : une seule modification par type
            foreach (List<TypeEdit> edits in typeEdits.Values)
            {
                ChangeItem first = edits[0].Item;
                var distinct = edits.Select(e => e.Canonical).Distinct().ToList();
                if (distinct.Count > 1)
                {
                    first.Status = ChangeStatus.Error;
                    first.Apply = false;
                    first.NewValue = string.Join(" / ", distinct);
                    first.Message = "Valeurs différentes saisies pour le même type : harmonisez-les dans Excel.";
                }
                else
                {
                    if (edits.Any(e => e.Item.Status == ChangeStatus.Conflict) && first.Status == ChangeStatus.Change)
                    {
                        first.Status = ChangeStatus.Conflict;
                        first.Apply = false;
                    }
                    string note = $"Paramètre de type, modifié sur {edits.Count} ligne(s) : s'applique à toutes les occurrences du type.";
                    first.Message = string.IsNullOrEmpty(first.Message) ? note : first.Message + " " + note;
                }
                result.Items.Add(first);
            }

            if (orphanRows > 0)
                result.Warnings.Add($"Feuille « {sheetName} » : {orphanRows} ligne(s) sans identifiant ignorée(s). La création d'éléments n'est pas prise en charge.");
        }

        private static Dictionary<string, Dictionary<string, string>> ReadSnapshot(IXLWorksheet snap)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            int lastCol = snap.Row(KeyRow).LastCellUsed()?.Address.ColumnNumber ?? 0;
            int lastRow = snap.LastRowUsed()?.RowNumber() ?? 0;

            var keys = new Dictionary<int, string>();
            int uidCol = -1;
            for (int c = 1; c <= lastCol; c++)
            {
                string k = snap.Cell(KeyRow, c).GetString();
                if (k == KeyUid) uidCol = c;
                else if (!k.StartsWith("#"))
                {
                    ColumnSpec spec = ColumnSpec.TryParse(k, "");
                    if (spec != null) keys[c] = spec.ColumnKey;
                }
            }
            if (uidCol < 0) return result;

            for (int r = FirstDataRow; r <= lastRow; r++)
            {
                string uid = snap.Cell(r, uidCol).GetString();
                if (string.IsNullOrEmpty(uid)) continue;
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in keys)
                    row[kv.Value] = (snap.Cell(r, kv.Key).GetString() ?? "").Replace("\r\n", "\n");
                result[uid] = row;
            }
            return result;
        }

        private static string Label(Element e)
        {
            if (e is ElementType t) return string.IsNullOrEmpty(t.FamilyName) ? t.Name : t.FamilyName + " : " + t.Name;
            ElementId typeId = e.GetTypeId();
            if (typeId != ElementId.InvalidElementId && e.Document.GetElement(typeId) is ElementType et)
                return string.IsNullOrEmpty(et.FamilyName) ? et.Name : et.FamilyName + " : " + et.Name;
            return e.Name;
        }
    }
}
