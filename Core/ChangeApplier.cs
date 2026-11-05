using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

namespace RevitExcelLink.Core
{
    public sealed class ApplyResult
    {
        public int Applied { get; set; }
        public int Failed { get; set; }
        public bool RolledBack { get; set; }
    }

    /// <summary>Applique les modifications cochées dans une seule transaction (un seul Ctrl+Z dans Revit).</summary>
    public static class ChangeApplier
    {
        public static ApplyResult Apply(Document doc, IList<ChangeItem> items)
        {
            var result = new ApplyResult();
            var todo = items.Where(i => i.Apply && i.CanApply).ToList();
            if (todo.Count == 0) return result;

            using (var t = new Transaction(doc, "Import Excel"))
            {
                FailureHandlingOptions options = t.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(new WarningSwallower());
                t.SetFailureHandlingOptions(options);
                t.Start();

                foreach (ChangeItem item in todo)
                {
                    try
                    {
                        string error = ApplyOne(doc, item);
                        item.Succeeded = error == null;
                        item.Result = error ?? "Appliqué";
                    }
                    catch (Exception ex)
                    {
                        item.Succeeded = false;
                        item.Result = "Échec : " + ex.Message;
                    }
                }

                TransactionStatus status = t.Commit();
                if (status != TransactionStatus.Committed)
                {
                    result.RolledBack = true;
                    foreach (ChangeItem item in todo.Where(i => i.Succeeded))
                    {
                        item.Succeeded = false;
                        item.Result = "Annulé : Revit a refusé la transaction.";
                    }
                }
            }

            result.Applied = todo.Count(i => i.Succeeded);
            result.Failed = todo.Count - result.Applied;
            return result;
        }

        private static string ApplyOne(Document doc, ChangeItem item)
        {
            Element e = doc.GetElement(item.TargetUid);
            if (e == null) return "Échec : élément introuvable.";

            if (doc.IsWorkshared)
            {
                CheckoutStatus cs = WorksharingUtils.GetCheckoutStatus(doc, e.Id);
                if (cs == CheckoutStatus.OwnedByOtherUser)
                    return "Échec : élément réservé par un autre utilisateur.";
            }

            Parameter p = ParamKeys.Find(e, item.ParamKey);
            if (p == null) return "Échec : paramètre introuvable.";
            if (p.IsReadOnly) return "Échec : paramètre en lecture seule (élément de groupe ?).";

            bool ok;
            switch (item.Storage)
            {
                case StorageType.Double: ok = p.Set((double)item.Value); break;
                case StorageType.Integer: ok = p.Set((int)item.Value); break;
                case StorageType.String: ok = p.Set((string)item.Value ?? ""); break;
                case StorageType.ElementId: ok = p.Set((ElementId)item.Value); break;
                default: return "Échec : type de paramètre non pris en charge.";
            }
            return ok ? null : "Échec : Revit a refusé la valeur.";
        }

        /// <summary>Écrit un rapport texte à côté du fichier Excel (ou dans %TEMP% si impossible).</summary>
        public static string WriteReport(string excelPath, ImportAnalysis analysis, ApplyResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Import Excel → Revit");
            sb.AppendLine("Fichier : " + excelPath);
            sb.AppendLine("Date    : " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"));
            sb.AppendLine($"Appliquées : {result.Applied}   Échecs : {result.Failed}" + (result.RolledBack ? "   (TRANSACTION ANNULÉE)" : ""));
            sb.AppendLine();
            foreach (string w in analysis.Warnings) sb.AppendLine("AVERTISSEMENT : " + w);
            if (analysis.Warnings.Count > 0) sb.AppendLine();

            sb.AppendLine("Statut\tRésultat\tFeuille\tId\tÉlément\tParamètre\tAncienne valeur\tNouvelle valeur\tRemarque");
            foreach (ChangeItem i in analysis.Items)
            {
                string res = i.Result ?? (i.CanApply ? "Non coché" : "Non applicable");
                sb.AppendLine(string.Join("\t", i.StatusText, res, i.Sheet, i.ElementIdText, i.ElementLabel,
                    i.ParamName, Clean(i.OldValue), Clean(i.NewValue), Clean(i.Message)));
            }

            string name = Path.GetFileNameWithoutExtension(excelPath) + " – rapport d'import " + DateTime.Now.ToString("yyyy-MM-dd HH'h'mm") + ".txt";
            string target = Path.Combine(Path.GetDirectoryName(excelPath) ?? Path.GetTempPath(), name);
            try { File.WriteAllText(target, sb.ToString(), Encoding.UTF8); }
            catch
            {
                target = Path.Combine(Path.GetTempPath(), name);
                File.WriteAllText(target, sb.ToString(), Encoding.UTF8);
            }
            return target;
        }

        private static string Clean(string s) => (s ?? "").Replace("\t", " ").Replace("\n", " ");

        /// <summary>Supprime les avertissements Revit pour ne pas interrompre l'import.</summary>
        private sealed class WarningSwallower : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                foreach (FailureMessageAccessor f in accessor.GetFailureMessages())
                    if (f.GetSeverity() == FailureSeverity.Warning)
                        accessor.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }
    }
}
