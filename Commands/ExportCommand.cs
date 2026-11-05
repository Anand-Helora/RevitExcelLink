using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitExcelLink.Core;
using RevitExcelLink.UI;

namespace RevitExcelLink.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ExportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) { TaskDialog.Show("Helora BIM – Exporter vers Excel", "Ouvrez d'abord un projet."); return Result.Cancelled; }
            Document doc = uidoc.Document;
            if (doc.IsFamilyDocument) { TaskDialog.Show("Helora BIM – Exporter vers Excel", "Cette commande fonctionne dans un projet, pas dans l'éditeur de familles."); return Result.Cancelled; }

            try
            {
                var window = new ExportWindow(uidoc);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;
                if (window.ShowDialog() != true || window.Result == null) return Result.Cancelled;

                ExportRequest req = window.Result;
                ExportSummary summary;
                using (new WaitCursor())
                    summary = ExcelExporter.Export(doc, req);

                string text = $"{summary.Rows} ligne(s) exportée(s) dans {summary.Sheets} feuille(s).\n\n{req.FilePath}";
                if (summary.SkippedCategories.Count > 0)
                    text += "\n\nSans données, ignoré(es) : " + string.Join(", ", summary.SkippedCategories);
                if (summary.Notes.Count > 0)
                    text += "\n\n" + string.Join("\n", summary.Notes);

                // Le dialogue n'est affiché que s'il apporte une information (ou si le fichier ne s'ouvre pas)
                if (!req.OpenAfter || summary.Notes.Count > 0 || summary.SkippedCategories.Count > 0)
                    TaskDialog.Show("Helora BIM – Exporter vers Excel", text);
                if (req.OpenAfter)
                    Process.Start(new ProcessStartInfo(req.FilePath) { UseShellExecute = true });

                return Result.Succeeded;
            }
            catch (IOException ex)
            {
                TaskDialog.Show("Helora BIM – Exporter vers Excel", "Impossible d'écrire le fichier. Est-il ouvert dans Excel ?\n\n" + ex.Message);
                return Result.Failed;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Helora BIM – Exporter vers Excel", "L'export a échoué :\n\n" + ex.Message);
                return Result.Failed;
            }
        }
    }
}
