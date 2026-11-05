using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitExcelLink.Core;
using RevitExcelLink.UI;

namespace RevitExcelLink.Commands
{
    /// <summary>
    /// Recopie dans chaque élément ponctuel les valeurs du local qui le contient
    /// (pièces du modèle courant et/ou des modèles liés).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class FillElementsCommand : IExternalCommand
    {
        private const string Title = "Helora BIM – Remplir les éléments";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            if (uidoc == null) { TaskDialog.Show(Title, "Ouvrez d'abord un projet."); return Result.Cancelled; }
            Document doc = uidoc.Document;
            if (doc.IsFamilyDocument) { TaskDialog.Show(Title, "Cette commande fonctionne dans un projet."); return Result.Cancelled; }

            NumberingSettings settings = NumberingSettings.Load();

            // --- Sources de locaux
            int localRooms = RoomLocator.PlacedRooms(doc).Count();
            var linkInstances = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>().Where(l => l.GetLinkDocument() != null).ToList();
            var choices = new List<LinkChoice>();
            foreach (RevitLinkInstance link in linkInstances)
            {
                int n = RoomLocator.PlacedRooms(link.GetLinkDocument()).Count();
                if (n == 0) continue;
                choices.Add(new LinkChoice { InstanceId = link.Id.Value, Name = link.GetLinkDocument().Title, Detail = $"  ({n} pièces)" });
            }
            if (localRooms == 0 && choices.Count == 0)
            {
                TaskDialog.Show(Title, "Aucune pièce placée dans ce modèle ni dans les modèles liés chargés.");
                return Result.Cancelled;
            }

            ICollection<ElementId> selection = uidoc.Selection.GetElementIds();
            var window = new FillElementsWindow(settings, localRooms, choices, selection.Count > 0);
            new WindowInteropHelper(window).Owner = uiapp.MainWindowHandle;
            if (window.ShowDialog() != true) return Result.Cancelled;

            settings.OverwriteContents = window.Overwrite;
            settings.Save();

            // --- Éléments à traiter
            ICollection<ElementId> limit = null;
            switch (window.Scope)
            {
                case FillScope.Selection:
                    limit = selection;
                    break;
                case FillScope.ActiveView:
                    limit = new FilteredElementCollector(doc, doc.ActiveView.Id).OfClass(typeof(FamilyInstance)).ToElementIds();
                    break;
            }
            if (limit != null && limit.Count == 0)
            {
                TaskDialog.Show(Title, "Aucun élément à traiter dans cette portée.");
                return Result.Cancelled;
            }

            var selectedLinks = linkInstances.Where(l => choices.Any(c => c.IsChecked && c.InstanceId == l.Id.Value)).ToList();
            var locator = new RoomLocator(doc, window.UseLocalRooms, selectedLinks);

            // --- Remplissage
            int analysed = 0, updated = 0, unchanged = 0;
            var outside = new List<ElementId>();
            var problems = new List<string>();
            var cache = new Dictionary<string, RoomValues>();

            using (var t = new Transaction(doc, "Remplir les éléments depuis les locaux"))
            {
                FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(new SilentWarnings());
                t.SetFailureHandlingOptions(fho);
                t.Start();

                foreach (FamilyInstance fi in RoomLocator.PointFamilies(doc, limit).ToList())
                {
                    analysed++;
                    Room room = locator.FindFor(fi);
                    if (room == null) { outside.Add(fi.Id); continue; }

                    string key = room.Document.PathName + "|" + room.UniqueId;
                    if (!cache.TryGetValue(key, out RoomValues values))
                    {
                        values = RoomValues.FromRoom(room, settings);
                        if (string.IsNullOrWhiteSpace(values.Number)) values.Number = null;
                        cache[key] = values;
                    }

                    if (ElementValueWriter.Fill(fi, values, settings, window.Overwrite, problems) > 0) updated++;
                    else unchanged++;
                }

                if (updated > 0) t.Commit();
                else t.RollBack();
            }

            // --- Bilan
            var td = new TaskDialog(Title)
            {
                MainInstruction = $"{updated} élément(s) mis à jour",
                MainContent = $"{analysed} élément(s) analysé(s) dans {locator.RoomCount} local(aux).\n"
                              + (unchanged > 0 ? $"{unchanged} inchangé(s) : déjà rempli(s) ou sans paramètres Helora.\n" : "")
                              + (outside.Count > 0 ? $"{outside.Count} hors de tout local." : "")
                              + (updated > 0 ? "\n\nAnnulable avec Ctrl+Z." : ""),
                CommonButtons = TaskDialogCommonButtons.Close
            };
            if (outside.Count > 0)
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, $"Sélectionner les {outside.Count} élément(s) hors local",
                                  "Pour les repérer dans la vue");
            if (problems.Count > 0)
            {
                td.ExpandedContent = string.Join("\n", problems.Distinct().Take(20));
                td.FooterText = $"{problems.Count} avertissement(s) : voir le détail.";
            }
            if (td.Show() == TaskDialogResult.CommandLink1)
                uidoc.Selection.SetElementIds(outside);

            return updated > 0 ? Result.Succeeded : Result.Cancelled;
        }

        private sealed class SilentWarnings : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                foreach (FailureMessageAccessor f in accessor.GetFailureMessages())
                    if (f.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }
    }
}
