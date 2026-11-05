using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
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
    /// Remplit le Numéro Revit, HEL_BLOC, HEL_Etage, H_Num_GMAO, et les éléments contenus.
    /// Une palette flottante permet de sauter des numéros, revenir en arrière, annuler le dernier local ou terminer.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class RoomNumberingCommand : IExternalCommand
    {
        private const string Title = "Helora BIM – Numérotation des locaux";
        private enum DuplicateChoice { AssignAnyway, UseNextFree, CancelClick }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            if (uidoc == null) { TaskDialog.Show(Title, "Ouvrez d'abord un projet."); return Result.Cancelled; }
            Document doc = uidoc.Document;
            if (doc.IsFamilyDocument) { TaskDialog.Show(Title, "Cette commande fonctionne dans un projet."); return Result.Cancelled; }
            IntPtr revitHandle = uiapp.MainWindowHandle;

            // --- Fenêtre de départ
            NumberingSettings settings = NumberingSettings.Load();
            var index = new RoomNumberIndex(doc);
            var window = new RoomNumberingWindow(settings, index.AllNumbers);
            new WindowInteropHelper(window).Owner = revitHandle;
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

            var session = new NumberingSession(settings);
            var errors = new List<string>();
            var filter = new RoomPickFilter();
            RoomLocator locator = settings.FillContents ? new RoomLocator(doc, true, null) : null;

            // --- Palette flottante
            bool interruptPending = false;
            void Interrupt()
            {
                if (interruptPending) return;
                interruptPending = true;
                Native.Cancel(revitHandle);   // annule le clic en attente et rend la main à la vue
            }
            var palette = new NumberingPalette(session, Interrupt);
            new WindowInteropHelper(palette).Owner = revitHandle;
            PlacePalette(palette, settings, revitHandle);
            palette.Show();

            using (var group = new TransactionGroup(doc, "Numérotation des locaux"))
            {
                group.Start();
                RoomPainter painter = RoomPainter.Start(new UIDocumentProxy(doc, uidoc.RefreshActiveView));
                try
                {
                    while (!session.FinishRequested)
                    {
                        palette.Refresh();
                        string next = session.NumberOf(session.Current);
                        Reference picked;
                        try
                        {
                            interruptPending = false;
                            picked = uidoc.Selection.PickObject(ObjectType.Element, filter,
                                $"Helora BIM · Cliquez sur le local {next}   —   Échap : terminer");
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                        {
                            if (session.FinishRequested) break;
                            if (session.UndoRequested)
                            {
                                session.UndoRequested = false;
                                session.RepickRequested = false;
                                UndoLast(doc, session, index, painter);
                                uidoc.RefreshActiveView();
                                continue;
                            }
                            if (session.RepickRequested) { session.RepickRequested = false; continue; }
                            break;   // Échap tapé par l'utilisateur dans la vue = terminer
                        }

                        Room room = ResolveRoom(doc, picked);
                        if (room == null) continue;

                        // --- Numéro déjà utilisé par une autre pièce ?
                        List<ElementId> others = index.UsedBy(next, room.Id);
                        if (others.Count > 0)
                        {
                            int free = session.Current;
                            while (index.UsedBy(session.NumberOf(free), room.Id).Count > 0) free++;

                            DuplicateChoice dc = AskDuplicate(doc, session, next, others, free);
                            if (dc == DuplicateChoice.CancelClick) continue;
                            if (dc == DuplicateChoice.UseNextFree)
                            {
                                session.SkipTo(free);
                                next = session.NumberOf(session.Current);
                            }
                        }

                        NumberRoom(doc, session, room, next, locator, index, errors, painter);
                        uidoc.RefreshActiveView();
                    }

                    if (session.NumberedCount > 0) group.Assimilate();
                    else group.RollBack();
                }
                catch
                {
                    if (group.HasStarted()) group.RollBack();
                    throw;
                }
                finally
                {
                    painter?.Dispose();          // fin de la coloration temporaire
                    uidoc.RefreshActiveView();
                    settings.PaletteLeft = palette.Left;
                    settings.PaletteTop = palette.Top;
                    palette.CloseFromCommand();
                }
            }

            // Prochaine fois : on repart du numéro suivant
            settings.Start = session.Current;
            settings.Save();

            if (errors.Count > 0) ShowSummary(session, errors);   // message seulement en cas de problème
            return session.NumberedCount > 0 ? Result.Succeeded : Result.Cancelled;
        }

        // ------------------------------------------------------------------ numérotation d'un local

        private static void NumberRoom(Document doc, NumberingSession session, Room room, string number,
                                       RoomLocator locator, RoomNumberIndex index, List<string> errors,
                                       RoomPainter painter)
        {
            NumberingSettings s = session.Settings;
            string counter = session.CounterOf(session.Current);
            var entry = new HistoryEntry
            {
                Counter = session.Current,
                Number = number,
                RoomId = room.Id,
                RoomLabel = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "(sans nom)",
                PreviousRoomNumber = room.Number
            };

            // Éléments contenus dans le local
            var contents = new List<FamilyInstance>();
            if (locator != null)
            {
                Outline zone = RoomLocator.SearchZone(room);
                if (zone != null)
                    foreach (FamilyInstance fi in RoomLocator.PointFamilies(doc, null, zone))
                    {
                        Room found = locator.FindFor(fi);
                        if (found != null && found.Id.Value == room.Id.Value) contents.Add(fi);
                    }
            }

            // Instantané avant modification (pour « Annuler le dernier »)
            entry.Undo.Add(ParamSnapshot.RoomNumber(room));
            foreach (string name in new[] { s.ParamBloc, s.ParamEtage, s.ParamGmao })
                AddSnapshot(entry, ParamSnapshot.Take(room, name));
            foreach (FamilyInstance fi in contents)
                foreach (string name in new[] { s.ElemParamBloc, s.ElemParamEtage, s.ElemParamGmao, s.ElemParamLocal })
                    AddSnapshot(entry, ParamSnapshot.Take(fi, name));

            using (var t = new Transaction(doc, "Numéroter " + number))
            {
                SilenceWarnings(t);
                t.Start();

                var problems = new List<string>();
                try { room.Number = number; }
                catch (Exception ex) { problems.Add("Numéro : " + ex.Message); }
                AddProblem(problems, room, s.ParamBloc, s.Bloc);
                AddProblem(problems, room, s.ParamEtage, s.Etage);
                AddProblem(problems, room, s.ParamGmao, counter);

                var values = new RoomValues { Bloc = s.Bloc, Etage = s.Etage, Gmao = counter, Number = number };
                foreach (FamilyInstance fi in contents)
                    if (ElementValueWriter.Fill(fi, values, s, s.OverwriteContents, problems) > 0) entry.ElementCount++;

                if (t.Commit() == TransactionStatus.Committed)
                {
                    index.Move(room.Id, entry.PreviousRoomNumber, number);
                    painter?.MarkDone(room);
                    session.History.Insert(0, entry);
                    session.Current++;
                    if (problems.Count > 0) errors.Add($"{number} : {string.Join(" ; ", problems.Distinct())}");
                }
                else
                {
                    errors.Add($"{number} : Revit a annulé la modification.");
                }
            }
        }

        private static void UndoLast(Document doc, NumberingSession session, RoomNumberIndex index, RoomPainter painter)
        {
            HistoryEntry last = session.History.FirstOrDefault(h => !h.IsSkip);
            if (last == null) return;

            using (var t = new Transaction(doc, "Annuler " + last.Number))
            {
                SilenceWarnings(t);
                t.Start();
                foreach (ParamSnapshot snap in last.Undo) snap.Restore(doc);
                if (t.Commit() != TransactionStatus.Committed) return;
            }

            index.Move(last.RoomId, last.Number, last.PreviousRoomNumber);
            painter?.MarkPending(doc.GetElement(last.RoomId) as Room);
            int pos = session.History.IndexOf(last);
            session.History.RemoveRange(0, pos + 1);   // retire aussi les sauts faits après ce local
            session.Current = last.Counter;
        }

        private static void AddSnapshot(HistoryEntry entry, ParamSnapshot snap)
        {
            if (snap != null) entry.Undo.Add(snap);
        }

        private static void AddProblem(List<string> problems, Element e, string paramName, string value)
        {
            if (string.IsNullOrWhiteSpace(paramName)) return;
            string err = RoomParameterWriter.Set(e, paramName, value);
            if (err != null) problems.Add(err);
        }

        private static void SilenceWarnings(Transaction t)
        {
            FailureHandlingOptions fho = t.GetFailureHandlingOptions();
            fho.SetFailuresPreprocessor(new WarningSwallower());
            t.SetFailureHandlingOptions(fho);
        }

        private static Room ResolveRoom(Document doc, Reference r)
        {
            Element e = doc.GetElement(r);
            if (e is Room room) return room;
            if (e is RoomTag tag) return tag.Room;
            return null;
        }

        // ------------------------------------------------------------------ palette

        private static void PlacePalette(Window palette, NumberingSettings s, IntPtr revitHandle)
        {
            palette.WindowStartupLocation = WindowStartupLocation.Manual;
            Rect work = SystemParameters.WorkArea;
            if (s.PaletteLeft >= work.Left && s.PaletteLeft < work.Right - 50 && s.PaletteTop >= work.Top && s.PaletteTop < work.Bottom - 50)
            {
                palette.Left = s.PaletteLeft;
                palette.Top = s.PaletteTop;
            }
            else
            {
                // Par défaut : en haut à droite de l'écran, sous le ruban
                palette.Left = work.Right - 330;
                palette.Top = work.Top + 180;
            }
        }

        // ------------------------------------------------------------------ dialogues

        private static DuplicateChoice AskDuplicate(Document doc, NumberingSession s, string number, List<ElementId> others, int free)
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
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, $"Utiliser le numéro libre suivant : {s.NumberOf(free)}",
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

        private static void ShowSummary(NumberingSession session, List<string> errors)
        {
            var numbered = session.History.Where(h => !h.IsSkip).Reverse().ToList();
            if (numbered.Count == 0 && errors.Count == 0) return;

            List<int> skipped = session.SkippedNumbers.ToList();
            int elements = numbered.Sum(h => h.ElementCount);
            var td = new TaskDialog(Title)
            {
                MainInstruction = numbered.Count == 0 ? "Aucun local numéroté." : $"{numbered.Count} local(aux) numéroté(s)",
                MainContent = (numbered.Count > 0 ? $"De {numbered.First().Number} à {numbered.Last().Number}." : "")
                              + (session.Settings.FillContents && numbered.Count > 0 ? $"\n{elements} élément(s) contenu(s) mis à jour." : "")
                              + (skipped.Count > 0
                                  ? "\nNuméros sautés : " + string.Join(", ", skipped.Take(30).Select(session.CounterOf))
                                    + (skipped.Count > 30 ? "…" : "")
                                  : "")
                              + (numbered.Count > 0 ? "\n\nAnnulable en une seule fois avec Ctrl+Z." : ""),
                CommonButtons = TaskDialogCommonButtons.Close
            };
            if (errors.Count > 0)
            {
                td.ExpandedContent = string.Join("\n", errors.Take(20));
                td.FooterText = $"{errors.Count} avertissement(s) : voir le détail.";
            }
            td.Show();
        }

        // ------------------------------------------------------------------ outils

        private sealed class RoomPickFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Room || elem is RoomTag;
            public bool AllowReference(Reference reference, XYZ position) => true;
        }

        private sealed class WarningSwallower : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                foreach (FailureMessageAccessor f in accessor.GetFailureMessages())
                    if (f.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }

        /// <summary>Interrompt le clic en attente : redonne le focus à Revit puis lui envoie Échap.</summary>
        private static class Native
        {
            private const uint WM_KEYDOWN = 0x0100;
            private const uint WM_KEYUP = 0x0101;
            private const int VK_ESCAPE = 0x1B;

            [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
            [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            public static void Cancel(IntPtr revit)
            {
                if (revit == IntPtr.Zero) return;
                SetForegroundWindow(revit);
                PostMessage(revit, WM_KEYDOWN, (IntPtr)VK_ESCAPE, IntPtr.Zero);
                PostMessage(revit, WM_KEYUP, (IntPtr)VK_ESCAPE, IntPtr.Zero);
            }
        }
    }
}
