using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.Revit.DB;

namespace RevitExcelLink.Core
{
    /// <summary>Élément qui porte la valeur d'une colonne.</summary>
    public enum ParamOwner
    {
        Instance,     // l'élément de la ligne
        Type,         // son type
        FromRoom,     // pièce de départ (portes, fenêtres…)
        ToRoom,       // pièce d'arrivée
        Room,         // pièce qui contient l'élément
        Space,        // espace MEP qui contient l'élément
        ProjectInfo   // informations sur le projet
    }

    public enum ExportScope { WholeModel, ActiveView, Selection }

    /// <summary>Un paramètre proposé à l'export (identifié de façon stable, indépendamment de la langue).</summary>
    public sealed class ParamDescriptor
    {
        public string Key { get; set; }            // S:guid | B:builtInInt | N:base64(nom)
        public string Name { get; set; }
        public ParamOwner Owner { get; set; }
        public bool IsReadOnly { get; set; }
        public StorageType Storage { get; set; }
        public ForgeTypeId DataType { get; set; }
        public string Origin { get; set; }         // "partagé", "intégré", "projet/famille"
        public long PhaseId { get; set; } = -1;    // phase de la nomenclature, pour retrouver les pièces

        /// <summary>Identifiant unique dans une liste : propriétaire + clé.</summary>
        public string Id => Owners.Code(Owner) + "|" + Key;
    }

    /// <summary>Codes, libellés et résolution des « éléments liés » (pièce, espace, projet…).</summary>
    public static class Owners
    {
        public static string Code(ParamOwner o)
        {
            switch (o)
            {
                case ParamOwner.Type: return "T";
                case ParamOwner.FromRoom: return "F";
                case ParamOwner.ToRoom: return "O";
                case ParamOwner.Room: return "R";
                case ParamOwner.Space: return "E";
                case ParamOwner.ProjectInfo: return "P";
                default: return "I";
            }
        }

        public static bool TryParse(string code, out ParamOwner o)
        {
            switch (code)
            {
                case "I": o = ParamOwner.Instance; return true;
                case "T": o = ParamOwner.Type; return true;
                case "F": o = ParamOwner.FromRoom; return true;
                case "O": o = ParamOwner.ToRoom; return true;
                case "R": o = ParamOwner.Room; return true;
                case "E": o = ParamOwner.Space; return true;
                case "P": o = ParamOwner.ProjectInfo; return true;
                default: o = ParamOwner.Instance; return false;
            }
        }

        /// <summary>Valeur partagée par plusieurs lignes (type, pièce…) : une modification s'applique à toutes.</summary>
        public static bool IsShared(ParamOwner o) => o != ParamOwner.Instance;

        public static bool IsRelated(ParamOwner o) => o != ParamOwner.Instance && o != ParamOwner.Type;

        public static string Label(ParamOwner o)
        {
            switch (o)
            {
                case ParamOwner.Type: return "Paramètre de type";
                case ParamOwner.FromRoom: return "Paramètre de la pièce de départ";
                case ParamOwner.ToRoom: return "Paramètre de la pièce d'arrivée";
                case ParamOwner.Room: return "Paramètre de la pièce";
                case ParamOwner.Space: return "Paramètre de l'espace";
                case ParamOwner.ProjectInfo: return "Information sur le projet";
                default: return "Paramètre d'occurrence";
            }
        }

        public static string SharedNote(ParamOwner o)
        {
            switch (o)
            {
                case ParamOwner.Type: return "toutes les occurrences du type";
                case ParamOwner.ProjectInfo: return "tout le projet";
                case ParamOwner.Space: return "l'espace (et toutes ses lignes)";
                default: return "la pièce (et toutes ses lignes)";
            }
        }

        /// <summary>Retrouve l'élément qui porte la valeur pour la ligne <paramref name="e"/>.</summary>
        public static Element Resolve(Document doc, Element e, ParamOwner owner, long phaseId, bool rowsAreTypes)
        {
            if (e == null) return null;
            try
            {
                switch (owner)
                {
                    case ParamOwner.Instance: return e;
                    case ParamOwner.Type: return rowsAreTypes ? e : doc.GetElement(e.GetTypeId());
                    case ParamOwner.ProjectInfo: return doc.ProjectInformation;
                }

                Phase phase = phaseId > 0 ? doc.GetElement(new ElementId(phaseId)) as Phase : null;
                if (e is FamilyInstance fi)
                {
                    switch (owner)
                    {
                        case ParamOwner.FromRoom: return phase != null ? fi.get_FromRoom(phase) : fi.FromRoom;
                        case ParamOwner.ToRoom: return phase != null ? fi.get_ToRoom(phase) : fi.ToRoom;
                        case ParamOwner.Room: return phase != null ? fi.get_Room(phase) : fi.Room;
                        case ParamOwner.Space: return phase != null ? fi.get_Space(phase) : fi.Space;
                    }
                }

                // Autres éléments : pièce ou espace au point d'insertion
                XYZ point = (e.Location as LocationPoint)?.Point
                            ?? (e.Location as LocationCurve)?.Curve?.Evaluate(0.5, true);
                if (point == null) return null;
                if (owner == ParamOwner.Room)
                    return phase != null ? doc.GetRoomAtPoint(point, phase) : doc.GetRoomAtPoint(point);
                if (owner == ParamOwner.Space && phase != null)
                    return doc.GetSpaceAtPoint(point, phase);
            }
            catch
            {
                // élément sans pièce, phase invalide…
            }
            return null;
        }
    }

    public static class ParamKeys
    {
        public static string KeyOf(Parameter p)
        {
            if (p.IsShared) return "S:" + p.GUID.ToString("D");
            if (p.Definition is InternalDefinition idf && idf.BuiltInParameter != BuiltInParameter.INVALID)
                return "B:" + ((int)idf.BuiltInParameter).ToString();
            return "N:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(p.Definition.Name));
        }

        public static string OriginOf(Parameter p)
        {
            if (p.IsShared) return "Paramètre partagé";
            if (p.Definition is InternalDefinition idf && idf.BuiltInParameter != BuiltInParameter.INVALID)
                return "Paramètre intégré Revit";
            return "Paramètre de projet ou de famille";
        }

        public static Parameter Find(Element e, string key)
        {
            if (e == null || string.IsNullOrEmpty(key) || key.Length < 3) return null;
            try
            {
                string v = key.Substring(2);
                switch (key[0])
                {
                    case 'S': return e.get_Parameter(Guid.Parse(v));
                    case 'B': return e.get_Parameter((BuiltInParameter)int.Parse(v));
                    case 'N': return e.LookupParameter(Encoding.UTF8.GetString(Convert.FromBase64String(v)));
                }
            }
            catch
            {
                // clé invalide ou paramètre inaccessible
            }
            return null;
        }

        public static ForgeTypeId SafeDataType(Parameter p)
        {
            try { return p.Definition.GetDataType(); }
            catch { return new ForgeTypeId(); }
        }
    }

    /// <summary>Une catégorie trouvée dans la portée choisie, avec ses éléments.</summary>
    public sealed class CategoryData
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public bool IsAnnotation { get; set; }
        public List<ElementId> ElementIds { get; } = new List<ElementId>();
    }

    public enum ExportKind { Categories, Schedules }

    public sealed class ExportRequest
    {
        public ExportKind Kind { get; set; } = ExportKind.Categories;
        public List<ElementId> ScheduleIds { get; set; } = new List<ElementId>();
        public bool ScheduleFormatted { get; set; }   // true = mise en forme Revit, non réimportable

        public List<CategoryData> Categories { get; set; } = new List<CategoryData>();
        public List<ParamDescriptor> Parameters { get; set; } = new List<ParamDescriptor>();
        public bool ByType { get; set; }
        public string FilePath { get; set; }
        public bool OpenAfter { get; set; }
    }
}
