using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitExcelLink.Core
{
    /// <summary>Valeurs d'un local à recopier dans les éléments qu'il contient.</summary>
    public sealed class RoomValues
    {
        public string Bloc { get; set; }
        public string Etage { get; set; }
        public string Gmao { get; set; }
        public string Number { get; set; }

        public static RoomValues FromRoom(Room room, NumberingSettings s) => new RoomValues
        {
            Bloc = Read(room, s.ParamBloc),
            Etage = Read(room, s.ParamEtage),
            Gmao = Read(room, s.ParamGmao),
            Number = room.Number
        };

        private static string Read(Element e, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            Parameter p = e.LookupParameter(name);
            if (p == null || !p.HasValue) return null;
            switch (p.StorageType)
            {
                case StorageType.String: return p.AsString();
                case StorageType.Integer: return p.AsInteger().ToString(CultureInfo.InvariantCulture);
                default: return p.AsValueString();
            }
        }
    }

    /// <summary>
    /// Trouve le local qui contient un point, parmi les pièces du modèle et/ou des modèles liés.
    /// Le test se fait en plan, à la hauteur du sol du local : un détecteur au plafond (au-dessus de la hauteur
    /// de la pièce) est donc bien rattaché à son local. En cas de locaux superposés, le plus haut sous le point gagne.
    /// </summary>
    public sealed class RoomLocator
    {
        private const double BelowTolerance = 0.5;      // pieds (~15 cm) sous le sol du local
        private const double AboveMargin = 8.2;         // pieds (~2,5 m) au-dessus de la hauteur de la pièce
        private const double XyTolerance = 0.05;

        private sealed class Region
        {
            public Room Room;
            public Transform HostToSource;   // null = pièce du modèle courant
            public XYZ Min, Max;             // boîte en coordonnées du modèle courant
            public double SourceBottomZ;     // sol du local, coordonnées de son propre modèle
            public double HostBottomZ, HostTopZ;
        }

        private readonly List<Region> _regions = new List<Region>();

        public int RoomCount => _regions.Count;

        public RoomLocator(Document host, bool includeLocal, IEnumerable<RevitLinkInstance> links)
        {
            if (includeLocal)
                foreach (Room r in PlacedRooms(host)) Add(r, null);

            foreach (RevitLinkInstance link in links ?? Enumerable.Empty<RevitLinkInstance>())
            {
                Document linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;   // lien déchargé
                Transform t = link.GetTotalTransform();
                foreach (Room r in PlacedRooms(linkDoc)) Add(r, t);
            }
        }

        public static IEnumerable<Room> PlacedRooms(Document d) =>
            new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Room>().Where(r => r.Area > 0 && r.Location != null);

        private void Add(Room room, Transform sourceToHost)
        {
            BoundingBoxXYZ bb = room.get_BoundingBox(null);
            if (bb == null) return;

            var region = new Region { Room = room, SourceBottomZ = bb.Min.Z };
            if (sourceToHost == null || sourceToHost.IsIdentity)
            {
                region.Min = bb.Min;
                region.Max = bb.Max;
            }
            else
            {
                region.HostToSource = sourceToHost.Inverse;
                var corners = new List<XYZ>();
                foreach (double x in new[] { bb.Min.X, bb.Max.X })
                    foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
                        foreach (double z in new[] { bb.Min.Z, bb.Max.Z })
                            corners.Add(sourceToHost.OfPoint(new XYZ(x, y, z)));
                region.Min = new XYZ(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z));
                region.Max = new XYZ(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z));
            }
            region.HostBottomZ = region.Min.Z;
            region.HostTopZ = region.Max.Z;
            _regions.Add(region);
        }

        /// <summary>Local qui contient le point (coordonnées du modèle courant), ou null.</summary>
        public Room Find(XYZ p)
        {
            Region best = null;
            foreach (Region r in _regions)
            {
                if (p.X < r.Min.X - XyTolerance || p.X > r.Max.X + XyTolerance) continue;
                if (p.Y < r.Min.Y - XyTolerance || p.Y > r.Max.Y + XyTolerance) continue;
                if (p.Z < r.HostBottomZ - BelowTolerance || p.Z > r.HostTopZ + AboveMargin) continue;
                if (best != null && r.HostBottomZ <= best.HostBottomZ) continue;

                XYZ ps = r.HostToSource != null ? r.HostToSource.OfPoint(p) : p;
                var test = new XYZ(ps.X, ps.Y, r.SourceBottomZ + 1.0);
                bool inside;
                try { inside = r.Room.IsPointInRoom(test); }
                catch { inside = false; }
                if (inside) best = r;
            }
            return best?.Room;
        }

        /// <summary>Point de référence d'un élément : point d'insertion, ou milieu de la ligne d'implantation.</summary>
        public static XYZ PointOf(Element e)
        {
            if (e.Location is LocationPoint lp) return lp.Point;
            if (e.Location is LocationCurve lc && lc.Curve != null)
            {
                try { return lc.Curve.Evaluate(0.5, true); } catch { }
            }
            return null;
        }

        private static readonly double[] SearchDistances = { 0.35, 0.8, 1.6 };   // pieds : ~10, 25 et 50 cm

        /// <summary>
        /// Local qui contient l'élément. Si son point tombe sur une limite (élément posé sur un mur),
        /// on cherche juste devant lui (côté où il est orienté), puis tout autour.
        /// </summary>
        public Room FindFor(Element e)
        {
            XYZ p = PointOf(e);
            if (p == null) return null;
            Room room = Find(p);
            if (room != null) return room;

            var directions = new List<XYZ>();
            if (e is FamilyInstance fi)
            {
                try
                {
                    XYZ f = fi.FacingOrientation;
                    if (f != null)
                    {
                        var flat = new XYZ(f.X, f.Y, 0);
                        if (!flat.IsZeroLength()) { directions.Add(flat.Normalize()); directions.Add(flat.Normalize().Negate()); }
                    }
                }
                catch { }
            }
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                directions.Add(new XYZ(Math.Cos(a), Math.Sin(a), 0));
            }

            foreach (double d in SearchDistances)
                foreach (XYZ dir in directions)
                {
                    room = Find(p + dir * d);
                    if (room != null) return room;
                }
            return null;
        }

        /// <summary>Familles du modèle (équipements, appareils, mobilier, luminaires…), hors portes, fenêtres et poutres.</summary>
        public static IEnumerable<FamilyInstance> PointFamilies(Document doc, ICollection<ElementId> limitTo = null, Outline zone = null)
        {
            FilteredElementCollector col = limitTo != null && limitTo.Count > 0
                ? new FilteredElementCollector(doc, limitTo)
                : new FilteredElementCollector(doc);
            col = col.OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType();
            if (zone != null) col = col.WherePasses(new BoundingBoxIntersectsFilter(zone));

            foreach (FamilyInstance fi in col.Cast<FamilyInstance>())
            {
                if (fi.ViewSpecific || fi.Category == null || fi.Category.CategoryType != CategoryType.Model) continue;
                if (!(fi.Location is LocationPoint) && !(fi.Location is LocationCurve)) continue;
                long cat = fi.Category.Id.Value;
                if (cat == (long)BuiltInCategory.OST_Doors || cat == (long)BuiltInCategory.OST_Windows
                    || cat == (long)BuiltInCategory.OST_StructuralFraming || cat == (long)BuiltInCategory.OST_Rooms) continue;
                yield return fi;
            }
        }

        /// <summary>Zone de recherche autour d'un local (coordonnées du modèle courant).</summary>
        public static Outline SearchZone(Room room)
        {
            BoundingBoxXYZ bb = room.get_BoundingBox(null);
            if (bb == null) return null;
            const double xy = 2.0;   // éléments posés sur les murs du local
            return new Outline(new XYZ(bb.Min.X - xy, bb.Min.Y - xy, bb.Min.Z - BelowTolerance),
                               new XYZ(bb.Max.X + xy, bb.Max.Y + xy, bb.Max.Z + AboveMargin));
        }
    }

    public static class ElementValueWriter
    {
        /// <summary>
        /// Écrit les valeurs du local dans l'élément. Les paramètres absents sont ignorés sans erreur.
        /// Renvoie le nombre de paramètres écrits.
        /// </summary>
        public static int Fill(Element e, RoomValues v, NumberingSettings s, bool overwrite, List<string> problems)
        {
            int written = 0;
            written += Write(e, s.ElemParamBloc, v.Bloc, overwrite, problems);
            written += Write(e, s.ElemParamEtage, v.Etage, overwrite, problems);
            written += Write(e, s.ElemParamGmao, v.Gmao, overwrite, problems);
            written += Write(e, s.ElemParamLocal, v.Number, overwrite, problems);
            return written;
        }

        private static int Write(Element e, string paramName, string value, bool overwrite, List<string> problems)
        {
            if (string.IsNullOrWhiteSpace(paramName) || value == null) return 0;
            Parameter p = e.LookupParameter(paramName);
            if (p == null || p.IsReadOnly) return 0;
            if (!overwrite && p.HasValue && !string.IsNullOrEmpty(p.AsString() ?? p.AsValueString())) return 0;

            string err = RoomParameterWriter.Set(e, paramName, value);
            if (err != null) { problems?.Add($"{e.Name} ({e.Id.Value}) : {err}"); return 0; }
            return 1;
        }
    }
}
