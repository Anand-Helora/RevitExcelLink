using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitExcelLink.Core
{
    /// <summary>
    /// Coloration temporaire des locaux de la vue active pendant la numérotation :
    /// rose pâle = à numéroter, vert pastel = numéroté pendant la session.
    /// Tout est restauré à la fin (remplacements graphiques, remplissage intérieur, gabarit de vue).
    /// </summary>
    public sealed class RoomHighlighter
    {
        private static readonly Color Pending = new Color(250, 214, 218);    // rose très clair
        private static readonly Color Done = new Color(205, 238, 212);       // vert pastel
        private const int Transparency = 30;

        private readonly Document _doc;
        private readonly View _view;
        private readonly ElementId _solidFill;
        private readonly Dictionary<long, OverrideGraphicSettings> _original = new Dictionary<long, OverrideGraphicSettings>();
        private ElementId _originalTemplate = ElementId.InvalidElementId;
        private ElementId _fillCategory = ElementId.InvalidElementId;
        private bool _fillWasHidden;

        private RoomHighlighter(Document doc, View view, ElementId solidFill)
        {
            _doc = doc;
            _view = view;
            _solidFill = solidFill;
        }

        /// <summary>Active la coloration (dans sa propre transaction). Renvoie null si la vue ne s'y prête pas.</summary>
        public static RoomHighlighter Start(Document doc, View view)
        {
            if (!(view is ViewPlan) && !(view is ViewSection)) return null;

            ElementId solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill && f.GetFillPattern().Target == FillPatternTarget.Drafting)?.Id;
            if (solid == null) return null;

            var h = new RoomHighlighter(doc, view, solid);
            using (var t = new Transaction(doc, "Affichage de la numérotation"))
            {
                t.Start();
                try
                {
                    // Un gabarit de vue peut verrouiller l'affichage du remplissage des pièces : on le retire le temps de la commande
                    if (view.ViewTemplateId != ElementId.InvalidElementId)
                    {
                        h._originalTemplate = view.ViewTemplateId;
                        view.ViewTemplateId = ElementId.InvalidElementId;
                    }

                    Category fill = Category.GetCategory(doc, BuiltInCategory.OST_RoomInteriorFill);
                    if (fill != null && view.CanCategoryBeHidden(fill.Id))
                    {
                        h._fillCategory = fill.Id;
                        h._fillWasHidden = view.GetCategoryHidden(fill.Id);
                        if (h._fillWasHidden) view.SetCategoryHidden(fill.Id, false);
                    }

                    foreach (Room r in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Rooms)
                                 .WhereElementIsNotElementType().OfType<Room>())
                    {
                        h._original[r.Id.Value] = view.GetElementOverrides(r.Id);
                        view.SetElementOverrides(r.Id, h.Make(Pending));
                    }
                    t.Commit();
                }
                catch
                {
                    t.RollBack();
                    return null;
                }
            }
            return h;
        }

        /// <summary>Local numéroté (à appeler dans une transaction ouverte).</summary>
        public void MarkDone(Room room) => Apply(room, Done);

        /// <summary>Local redevenu à numéroter, ex. après « Annuler le dernier » (dans une transaction ouverte).</summary>
        public void MarkPending(Room room) => Apply(room, Pending);

        private void Apply(Room room, Color color)
        {
            if (room == null) return;
            try
            {
                if (!_original.ContainsKey(room.Id.Value)) _original[room.Id.Value] = _view.GetElementOverrides(room.Id);
                _view.SetElementOverrides(room.Id, Make(color));
            }
            catch { }
        }

        private OverrideGraphicSettings Make(Color color)
        {
            var ogs = new OverrideGraphicSettings();
            ogs.SetSurfaceForegroundPatternId(_solidFill);
            ogs.SetSurfaceForegroundPatternColor(color);
            ogs.SetSurfaceForegroundPatternVisible(true);
            ogs.SetCutForegroundPatternId(_solidFill);
            ogs.SetCutForegroundPatternColor(color);
            ogs.SetCutForegroundPatternVisible(true);
            ogs.SetSurfaceTransparency(Transparency);
            return ogs;
        }

        /// <summary>Remet l'affichage de la vue exactement comme avant (dans sa propre transaction).</summary>
        public void Restore()
        {
            using (var t = new Transaction(_doc, "Restaurer l'affichage"))
            {
                t.Start();
                try
                {
                    foreach (var kv in _original)
                    {
                        var id = new ElementId(kv.Key);
                        if (_doc.GetElement(id) != null) _view.SetElementOverrides(id, kv.Value);
                    }
                    if (_fillCategory != ElementId.InvalidElementId && _fillWasHidden)
                        _view.SetCategoryHidden(_fillCategory, true);
                    if (_originalTemplate != ElementId.InvalidElementId)
                        _view.ViewTemplateId = _originalTemplate;
                    t.Commit();
                }
                catch
                {
                    t.RollBack();
                }
            }
        }
    }
}
