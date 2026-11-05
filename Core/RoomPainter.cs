using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;

namespace RevitExcelLink.Core
{
    /// <summary>
    /// Colore les locaux de la vue en plan active pendant la numérotation, par un affichage temporaire
    /// (DirectContext3D) : rien n'est écrit dans le modèle, rien n'entre dans l'historique d'annulation.
    /// Rose pâle = à numéroter, vert pastel = numéroté pendant la session.
    /// </summary>
    public sealed class RoomPainter : IDirectContext3DServer, IDisposable
    {
        private static readonly Guid ServerGuid = new Guid("3B4E2C6A-1D7F-4E59-9C0B-8A2F61D4E7A3");

        // Couleurs (transparence : 0 = opaque, 255 = invisible)
        private static readonly ColorWithTransparency PendingFill = new ColorWithTransparency(246, 160, 170, 150);
        private static readonly ColorWithTransparency DoneFill = new ColorWithTransparency(120, 205, 140, 150);
        private static readonly ColorWithTransparency PendingEdge = new ColorWithTransparency(214, 96, 110, 0);
        private static readonly ColorWithTransparency DoneEdge = new ColorWithTransparency(52, 150, 84, 0);

        private const double Lift = 0.3;   // pieds (~9 cm) au-dessus du sol du local, sous le mobilier

        private sealed class Shape
        {
            public List<XYZ> Triangles = new List<XYZ>();   // 3 points par triangle
            public List<XYZ> Edges = new List<XYZ>();       // 2 points par segment
            public bool Done;
        }

        private readonly Document _doc;
        private readonly ElementId _viewId;
        private readonly Dictionary<long, Shape> _shapes = new Dictionary<long, Shape>();
        private readonly Outline _bounds;
        private bool _registered;

        private RoomPainter(Document doc, View view)
        {
            _doc = doc;
            _viewId = view.Id;

            XYZ min = null, max = null;
            foreach (Room room in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Rooms)
                         .WhereElementIsNotElementType().OfType<Room>().Where(r => r.Area > 0))
            {
                Shape shape = BuildShape(room);
                if (shape == null || shape.Triangles.Count == 0) continue;
                _shapes[room.Id.Value] = shape;
                foreach (XYZ p in shape.Triangles)
                {
                    min = min == null ? p : new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                    max = max == null ? p : new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
                }
            }
            if (min != null) _bounds = new Outline(min - new XYZ(1, 1, 1), max + new XYZ(1, 1, 1));
        }

        /// <summary>Démarre la coloration dans la vue en plan active. Renvoie null si impossible.</summary>
        public static RoomPainter Start(UIDocumentProxy ui)
        {
            try
            {
                View view = ui.Document.ActiveView;
                if (!(view is ViewPlan plan) || plan.ViewType == ViewType.CeilingPlan) return null;

                var painter = new RoomPainter(ui.Document, view);
                if (painter._shapes.Count == 0) return null;

                var service = ExternalServiceRegistry.GetService(ExternalServices.BuiltInExternalServices.DirectContext3DService)
                              as MultiServerService;
                if (service == null) return null;

                if (service.IsRegisteredServerId(ServerGuid)) service.RemoveServer(ServerGuid);
                service.AddServer(painter);
                IList<Guid> active = service.GetActiveServerIds();
                if (!active.Contains(ServerGuid)) active.Add(ServerGuid);
                service.SetActiveServers(active);
                painter._registered = true;
                ui.Refresh();
                return painter;
            }
            catch
            {
                return null;
            }
        }

        public void MarkDone(Room room) => Set(room, true);
        public void MarkPending(Room room) => Set(room, false);

        private void Set(Room room, bool done)
        {
            if (room != null && _shapes.TryGetValue(room.Id.Value, out Shape s)) s.Done = done;
        }

        public void Dispose()
        {
            if (!_registered) return;
            _registered = false;
            try
            {
                var service = ExternalServiceRegistry.GetService(ExternalServices.BuiltInExternalServices.DirectContext3DService)
                              as MultiServerService;
                if (service == null) return;
                IList<Guid> active = service.GetActiveServerIds();
                if (active.Remove(ServerGuid)) service.SetActiveServers(active);
                if (service.IsRegisteredServerId(ServerGuid)) service.RemoveServer(ServerGuid);
            }
            catch { }
        }

        // ------------------------------------------------------------------ géométrie (préparée une fois)

        private static Shape BuildShape(Room room)
        {
            var shape = new Shape();
            double z;
            try
            {
                BoundingBoxXYZ bb = room.get_BoundingBox(null);
                if (bb == null) return null;
                z = bb.Min.Z + Lift;
            }
            catch { return null; }

            // Surface : face inférieure du volume de la pièce, triangulée
            try
            {
                GeometryElement shell = room.ClosedShell;
                if (shell != null)
                    foreach (GeometryObject obj in shell)
                    {
                        if (!(obj is Solid solid) || solid.Faces.IsEmpty) continue;
                        foreach (Face face in solid.Faces)
                        {
                            if (!(face is PlanarFace pf) || pf.FaceNormal.Z > -0.9) continue;
                            Mesh mesh = pf.Triangulate();
                            for (int i = 0; i < mesh.NumTriangles; i++)
                            {
                                MeshTriangle tri = mesh.get_Triangle(i);
                                for (int k = 0; k < 3; k++)
                                {
                                    XYZ v = tri.get_Vertex(k);
                                    shape.Triangles.Add(new XYZ(v.X, v.Y, z));
                                }
                            }
                        }
                    }
            }
            catch { }

            // Contour : limites de la pièce
            try
            {
                foreach (IList<BoundarySegment> loop in room.GetBoundarySegments(new SpatialElementBoundaryOptions()))
                    foreach (BoundarySegment seg in loop)
                    {
                        IList<XYZ> pts = seg.GetCurve().Tessellate();
                        for (int i = 0; i + 1 < pts.Count; i++)
                        {
                            shape.Edges.Add(new XYZ(pts[i].X, pts[i].Y, z + 0.01));
                            shape.Edges.Add(new XYZ(pts[i + 1].X, pts[i + 1].Y, z + 0.01));
                        }
                    }
            }
            catch { }

            return shape;
        }

        // ------------------------------------------------------------------ IDirectContext3DServer

        public Guid GetServerId() => ServerGuid;
        public ExternalServiceId GetServiceId() => ExternalServices.BuiltInExternalServices.DirectContext3DService;
        public string GetName() => "Helora BIM – Numérotation des locaux";
        public string GetVendorId() => "HELORA";
        public string GetDescription() => "Coloration temporaire des locaux pendant la numérotation";
        public string GetApplicationId() => "";
        public string GetSourceId() => "";
        public bool UsesHandles() => false;
        public bool UseInTransparentPass(View view) => true;
        public Outline GetBoundingBox(View view) => _bounds;

        public bool CanExecute(View view) =>
            _registered && view != null && view.Id == _viewId && view.Document.Equals(_doc);

        public void RenderScene(View view, DisplayStyle displayStyle)
        {
            try
            {
                if (DrawContext.IsTransparentPass())
                {
                    DrawTriangles(_shapes.Values.Where(s => !s.Done).SelectMany(s => s.Triangles).ToList(), PendingFill);
                    DrawTriangles(_shapes.Values.Where(s => s.Done).SelectMany(s => s.Triangles).ToList(), DoneFill);
                }
                else
                {
                    DrawLines(_shapes.Values.Where(s => !s.Done).SelectMany(s => s.Edges).ToList(), PendingEdge);
                    DrawLines(_shapes.Values.Where(s => s.Done).SelectMany(s => s.Edges).ToList(), DoneEdge);
                }
            }
            catch
            {
                // un échec d'affichage ne doit jamais perturber Revit
            }
        }

        private const int MaxVerticesPerBatch = 60000;   // les index sont sur 16 bits

        private static void DrawTriangles(List<XYZ> points, ColorWithTransparency color)
        {
            for (int start = 0; start < points.Count; start += MaxVerticesPerBatch)
            {
                int count = Math.Min(MaxVerticesPerBatch, points.Count - start);
                count -= count % 3;
                if (count == 0) continue;
                int triCount = count / 3;

                var vb = new VertexBuffer(count * VertexPositionColored.GetSizeInFloats());
                vb.Map(count * VertexPositionColored.GetSizeInFloats());
                VertexStreamPositionColored vs = vb.GetVertexStreamPositionColored();
                for (int i = 0; i < count; i++) vs.AddVertex(new VertexPositionColored(points[start + i], color));
                vb.Unmap();

                var ib = new IndexBuffer(triCount * IndexTriangle.GetSizeInShortInts());
                ib.Map(triCount * IndexTriangle.GetSizeInShortInts());
                IndexStreamTriangle istream = ib.GetIndexStreamTriangle();
                for (int t = 0; t < triCount; t++) istream.AddTriangle(new IndexTriangle(t * 3, t * 3 + 1, t * 3 + 2));
                ib.Unmap();

                var format = new VertexFormat(VertexFormatBits.PositionColored);
                var effect = new EffectInstance(VertexFormatBits.PositionColored);
                DrawContext.FlushBuffer(vb, count, ib, triCount * 3, format, effect, PrimitiveType.TriangleList, 0, triCount);
            }
        }

        private static void DrawLines(List<XYZ> points, ColorWithTransparency color)
        {
            for (int start = 0; start < points.Count; start += MaxVerticesPerBatch)
            {
                int count = Math.Min(MaxVerticesPerBatch, points.Count - start);
                count -= count % 2;
                if (count == 0) continue;
                int lineCount = count / 2;

                var vb = new VertexBuffer(count * VertexPositionColored.GetSizeInFloats());
                vb.Map(count * VertexPositionColored.GetSizeInFloats());
                VertexStreamPositionColored vs = vb.GetVertexStreamPositionColored();
                for (int i = 0; i < count; i++) vs.AddVertex(new VertexPositionColored(points[start + i], color));
                vb.Unmap();

                var ib = new IndexBuffer(lineCount * IndexLine.GetSizeInShortInts());
                ib.Map(lineCount * IndexLine.GetSizeInShortInts());
                IndexStreamLine istream = ib.GetIndexStreamLine();
                for (int l = 0; l < lineCount; l++) istream.AddLine(new IndexLine(l * 2, l * 2 + 1));
                ib.Unmap();

                var format = new VertexFormat(VertexFormatBits.PositionColored);
                var effect = new EffectInstance(VertexFormatBits.PositionColored);
                DrawContext.FlushBuffer(vb, count, ib, lineCount * 2, format, effect, PrimitiveType.LineList, 0, lineCount);
            }
        }
    }

    /// <summary>Petit adaptateur pour ne pas faire dépendre le Core de l'API UI.</summary>
    public sealed class UIDocumentProxy
    {
        public Document Document { get; }
        private readonly Action _refresh;
        public UIDocumentProxy(Document doc, Action refresh) { Document = doc; _refresh = refresh; }
        public void Refresh() => _refresh?.Invoke();
    }
}
