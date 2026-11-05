using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitExcelLink.Core;
using RevitExcelLink.UI;

namespace RevitExcelLink.Commands
{
    /// <summary>
    /// Numérote les locaux un par un : BLOC/CODE-ÉTAGE/NUMÉRO (ex. B/N00/015).
    /// Remplit le Numéro Revit, HEL_BLOC, HEL_Etage et H_Num_GMAO (compteur).
    /// Échap ouvre un menu : reprendre, sauter un numéro, aller au n°…, terminer.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class RoomNumberingCommand : IExternalCommand
    {
        private const string Title = "Helora BIM – Numérotation des locaux";

        private enum EscapeChoice { Resume, SkipOne, GoTo, Finish }
        private enum DuplicateChoice { AssignAnyway, UseNextFree, CancelClick }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            if (uidoc == null) { TaskDialog.Show(Title, "Ouvrez d'abord un projet."); return Result.Cancelled; }
            Document doc = uidoc.Document;
            if (doc.IsFamilyDocument) { TaskDialog.Show(Title, "Cette commande fonctionne dans un projet."); return Result.Cancelled; }

            // --- Fenêtre de départ
            NumberingSettings settings = NumberingSettings.Load();
            var index = new RoomNumberIndex(doc);
            var window = new RoomNumberingWindow(settings, index.AllNumbers);
            new WindowInteropHelper(window).Owner = uiapp.MainWindowHandle;
            if (window.ShowDialog() != true) return Result.Cancelled;
            settings.Save();

            List<string> missing = RoomParameterWriter.MissingParameters(doc, settings);
            if (missing.Count > 0)
            {
                var warn = new TaskDialog(Title)
                {
                    MainInstruction = "Paramètres introuvables sur les pièces",
                    MainContent = string.Join(", ", missing) + "\n\nLe Numéro Revit sera rempli, mais pas ces paramètres. Continuer ?",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No
                };
                if (warn.Show() != TaskDialogResult.Yes) return Result.Cancelled;
            }

            // --- Boucle de clics
            int current = settings.Start;
            var done = new List<string>();
            var skipped = new List<int>();
            var errors = new List<string>();
            var filter = new RoomPickFilter();

            using (var group = new TransactionGroup(doc, "Numérotation des locaux"))
            {
                group.Start();

                while (true)
                {
                    string next = NumberOf(settings, current);
                    Reference picked;
                    try
                    {
                        picked = uidoc.Selection.PickObject(ObjectType.Element, filter,
                            $"Helora BIM · Cliquez sur le local {next}   —   Échap : sauter des numéros ou terminer");
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        EscapeChoice choice = AskAfterEscape(settings, current, done.Count);
                        if (choice == EscapeChoice.Finish) break;
                        if (choice == EscapeChoice.SkipOne) { skipped.Add(current); current++; }
                        else if (choice == EscapeChoice.GoTo)
                        {
                            var input = new NumberInputWindow($"{settings.Bloc}/{settings.FloorCode}/", current + 1,
                                "Les numéros intermédiaires seront sautés. Un numéro inférieur permet de revenir en arrière.");
                            new WindowInteropHelper(input).Owner = uiapp.MainWindowHandle;
                            if (input.ShowDialog() == true)
                            {
                                for (int k = current; k < input.Value; k++) skipped.Add(k);
                                current = input.Value;
                            }
                        }
                        continue;
                    }

                    Room room = ResolveRoom(doc, picked);
                    if (room == null) continue;

                    // --- Numéro déjà utilisé par une autre pièce ?
                    List<ElementId> others = index.UsedBy(next, room.Id);
                    if (others.Count > 0)
                    {
                        int free = current;
                        while (index.UsedBy(NumberOf(settings, free), room.Id).Count > 0) free++;

                        DuplicateChoice dc = AskDuplicate(doc, settings, next, others, free);
                        if (dc == DuplicateChoice.CancelClick) continue;
                        if (dc == DuplicateChoice.UseNextFree)
                        {
                            for (int k = current; k < free; k++) skipped.Add(k);
                            current = free;
                            next = NumberOf(settings, current);
                        }
                    }

                    // --- Écriture
                    using (var t = new Transaction(doc, "Numéroter " + next))
                    {
                        FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                        fho.SetFailuresPreprocessor(new WarningSwallower());
                        t.SetFailureHandlingOptions(fho);
                        t.Start();

                        string oldNumber = room.Number;
                        var problems = new List<string>();
                        try { room.Number = next; }
                        catch (Exception ex) { problems.Add("Numéro : " + ex.Message); }

                        AddProblem(problems, room, settings.ParamBloc, settings.Bloc);
                        AddProblem(problems, room, settings.ParamEtage, settings.Etage);
                        AddProblem(problems, room, settings.ParamGmao, RoomNumberFormat.Counter(current, settings.Digits));

                        if (t.Commit() == TransactionStatus.Committed)
                        {
                            index.Move(room.Id, oldNumber, next);
                            done.Add(next);
                            if (problems.Count > 0) errors.Add($"{next} : {string.Join(" ; ", problems.Distinct())}");
                            current++;
                        }
                        else
                        {
                            errors.Add($"{next} : Revit a annulé la modification.");
                        }
                    }
                }

                if (done.Count > 0) group.Assimilate();
                else group.RollBack();
            }

            // Prochaine fois : on repart du numéro suivant
            settings.Start = current;
            settings.Save();

            ShowSummary(settings, done, skipped, errors);
            return done.Count > 0 ? Result.Succeeded : Result.Cancelled;
        }

        private static string NumberOf(NumberingSettings s, int n) => RoomNumberFormat.Full(s.Bloc, s.FloorCode, n, s.Digits);

        private static void AddProblem(List<string> problems, Element e, string paramName, string value)
        {
            if (string.IsNullOrWhiteSpace(paramName)) return;
            string err = RoomParameterWriter.Set(e, paramName, value);
            if (err != null) problems.Add(err);
        }

        private static Room ResolveRoom(Document doc, Reference r)
        {
            Element e = doc.GetElement(r);
            if (e is Room room) return room;
            if (e is RoomTag tag) return tag.Room;
            return null;
        }

        private static EscapeChoice AskAfterEscape(NumberingSettings s, int current, int doneCount)
        {
            var td = new TaskDialog(Title)
            {
                MainInstruction = $"Prochain numéro : {NumberOf(s, current)}",
                MainContent = doneCount == 0 ? "Aucun local numéroté pour l'instant." : $"{doneCount} local(aux) numéroté(s) pendant cette session.",
                FooterText = "Échap une seconde fois = terminer.",
                AllowCancellation = true
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Reprendre", $"Continuer avec {NumberOf(s, current)}");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Sauter 1 numéro", $"Le prochain local recevra {NumberOf(s, current + 1)}");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Aller au numéro…", "Sauter plusieurs numéros, ou revenir en arrière");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "Terminer", "Enregistrer la numérotation et quitter");

            switch (td.Show())
            {
                case TaskDialogResult.CommandLink1: return EscapeChoice.Resume;
                case TaskDialogResult.CommandLink2: return EscapeChoice.SkipOne;
                case TaskDialogResult.CommandLink3: return EscapeChoice.GoTo;
                default: return EscapeChoice.Finish;
            }
        }

        private static DuplicateChoice AskDuplicate(Document doc, NumberingSettings s, string number, List<ElementId> others, int free)
        {
            string who = string.Join("\n", others.Take(5).Select(id =>
            {
                Room r = doc.GetElement(id) as Room;
                string name = r?.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                string level = r?.Level?.Name;
                return $"• {name ?? "(sans nom)"}" + (string.IsNullOrEmpty(level) ? "" : $" – {level}");
            }));

            var td = new TaskDialog(Title)
            {
                MainInstruction = $"Le numéro {number} est déjà utilisé",
                MainContent = "Par :\n" + who,
                AllowCancellation = true
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, $"Utiliser le numéro libre suivant : {NumberOf(s, free)}",
                              "Les numéros occupés sont sautés");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, $"Attribuer quand même {number}", "Crée un doublon");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Annuler ce clic", "Rien n'est modifié");

            switch (td.Show())
            {
                case TaskDialogResult.CommandLink1: return DuplicateChoice.UseNextFree;
                case TaskDialogResult.CommandLink2: return DuplicateChoice.AssignAnyway;
                default: return DuplicateChoice.CancelClick;
            }
        }

        private static void ShowSummary(NumberingSettings s, List<string> done, List<int> skipped, List<string> errors)
        {
            if (done.Count == 0 && errors.Count == 0) return;

            var td = new TaskDialog(Title)
            {
                MainInstruction = done.Count == 0 ? "Aucun local numéroté." : $"{done.Count} local(aux) numéroté(s)",
                MainContent = (done.Count > 0 ? $"De {done.First()} à {done.Last()}." : "")
                              + (skipped.Count > 0
                                  ? "\nNuméros sautés : " + string.Join(", ", skipped.Distinct().OrderBy(n => n).Take(30)
                                        .Select(n => RoomNumberFormat.Counter(n, s.Digits)))
                                    + (skipped.Distinct().Count() > 30 ? "…" : "")
                                  : "")
                              + (done.Count > 0 ? "\n\nAnnulable en une seule fois avec Ctrl+Z." : ""),
                CommonButtons = TaskDialogCommonButtons.Close
            };
            if (errors.Count > 0)
            {
                td.ExpandedContent = string.Join("\n", errors.Take(20));
                td.FooterText = $"{errors.Count} avertissement(s) : voir le détail.";
            }
            td.Show();
        }

        /// <summary>Seules les pièces et leurs étiquettes sont sélectionnables.</summary>
        private sealed class RoomPickFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Room || elem is RoomTag;
            public bool AllowReference(Reference reference, XYZ position) => true;
        }

        /// <summary>Supprime les avertissements (ex. numéro en double choisi volontairement).</summary>
        private sealed class WarningSwallower : IFailuresPreprocessor
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
