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
            if (uidoc == null) { TaskDialog.Show("Exporter vers Excel", "Ouvrez d'abord un projet."); return Result.Cancelled; }
            Document doc = uidoc.Document;
            if (doc.IsFamilyDocument) { TaskDialog.Show("Exporter vers Excel", "Cette commande fonctionne dans un projet, pas dans l'éditeur de familles."); return Result.Cancelled; }

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
                    text += "\n\nCatégories sans données ignorées : " + string.Join(", ", summary.SkippedCategories);

                if (req.OpenAfter)
                    Process.Start(new ProcessStartInfo(req.FilePath) { UseShellExecute = true });
                else
                    TaskDialog.Show("Exporter vers Excel", text);

                return Result.Succeeded;
            }
            catch (IOException ex)
            {
                TaskDialog.Show("Exporter vers Excel", "Impossible d'écrire le fichier. Est-il ouvert dans Excel ?\n\n" + ex.Message);
                return Result.Failed;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Exporter vers Excel", "L'export a échoué :\n\n" + ex.Message);
                return Result.Failed;
            }
        }
    }
}
