using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.Revit.DB;

namespace RevitExcelLink.Core
{
    public enum ParamOwner { Instance, Type }

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

        /// <summary>Identifiant unique dans une liste : propriétaire + clé.</summary>
        public string Id => (Owner == ParamOwner.Type ? "T|" : "I|") + Key;
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
