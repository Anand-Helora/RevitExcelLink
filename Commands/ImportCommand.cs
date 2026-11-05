using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using RevitExcelLink.Core;
using RevitExcelLink.UI;

namespace RevitExcelLink.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ImportCommand : IExternalCommand
    {
        private const string Title = "Importer depuis Excel";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) { TaskDialog.Show(Title, "Ouvrez d'abord un projet."); return Result.Cancelled; }
            Document doc = uidoc.Document;

            var dlg = new OpenFileDialog
            {
                Filter = "Classeur Excel (*.xlsx)|*.xlsx",
                Title = "Choisir le fichier Excel à importer",
                InitialDirectory = UserSettings.LastFolder
            };
            if (dlg.ShowDialog() != true) return Result.Cancelled;
            string path = dlg.FileName;
            UserSettings.LastFolder = Path.GetDirectoryName(path);

            try
            {
                ImportAnalysis analysis;
                using (new WaitCursor())
                    analysis = ExcelImporter.Analyze(doc, path);

                if (analysis.Items.Count == 0)
                {
                    string text = "Aucune modification détectée.\n\nVérifiez que le fichier a bien été enregistré dans Excel.";
                    if (analysis.Warnings.Count > 0) text += "\n\n" + string.Join("\n", analysis.Warnings);
                    TaskDialog.Show(Title, text);
                    return Result.Cancelled;
                }

                var window = new ImportPreviewWindow(analysis, Path.GetFileName(path));
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;
                if (window.ShowDialog() != true) return Result.Cancelled;

                ApplyResult result;
                using (new WaitCursor())
                    result = ChangeApplier.Apply(doc, analysis.Items);
                string report = ChangeApplier.WriteReport(path, analysis, result);

                var td = new TaskDialog(Title)
                {
                    MainInstruction = result.RolledBack
                        ? "Revit a annulé l'import."
                        : $"{result.Applied} modification(s) appliquée(s)" + (result.Failed > 0 ? $", {result.Failed} échec(s)." : "."),
                    MainContent = "Rapport détaillé :\n" + report,
                    CommonButtons = TaskDialogCommonButtons.Close
                };
                var failures = analysis.Items.Where(i => i.Result != null && !i.Succeeded).Take(15).ToList();
                if (failures.Count > 0)
                    td.ExpandedContent = string.Join("\n", failures.Select(f => $"{f.ElementLabel} – {f.ParamName} : {f.Result}"));
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Ouvrir le rapport");
                if (td.Show() == TaskDialogResult.CommandLink1)
                    Process.Start(new ProcessStartInfo(report) { UseShellExecute = true });

                return result.Applied > 0 ? Result.Succeeded : Result.Cancelled;
            }
            catch (IOException ex)
            {
                TaskDialog.Show(Title, "Impossible de lire le fichier :\n\n" + ex.Message);
                return Result.Failed;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Title, "L'import a échoué :\n\n" + ex.Message);
                return Result.Failed;
            }
        }
    }
}
