using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitExcelLink.Core
{
    /// <summary>Lit le modèle : catégories présentes dans la portée et paramètres disponibles.</summary>
    public static class ModelScanner
    {
        private const int SampleElementsPerCategory = 400;
        private const int SampleTypesPerCategory = 400;

        public static List<CategoryData> CollectCategories(UIDocument uidoc, ExportScope scope)
        {
            Document doc = uidoc.Document;
            IEnumerable<Element> source;

            switch (scope)
            {
                case ExportScope.ActiveView:
                    View view = doc.ActiveView;
                    if (view == null) return new List<CategoryData>();
                    try
                    {
                        source = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType();
                    }
                    catch
                    {
                        return new List<CategoryData>(); // vue non prise en charge par un collecteur
                    }
                    break;

                case ExportScope.Selection:
                    source = uidoc.Selection.GetElementIds()
                        .Select(id => doc.GetElement(id))
                        .Where(e => e != null);
                    break;

                default:
                    source = new FilteredElementCollector(doc).WhereElementIsNotElementType();
                    break;
            }

            var map = new Dictionary<long, CategoryData>();
            foreach (Element e in source)
            {
                if (e is ElementType) continue;
                Category cat = e.Category;
                if (cat == null) continue;
                if (cat.Parent != null) cat = cat.Parent; // ex. styles de lignes -> Lignes
                if (cat.CategoryType != CategoryType.Model && cat.CategoryType != CategoryType.Annotation) continue;

                long id = cat.Id.Value;
                if (!map.TryGetValue(id, out CategoryData data))
                {
                    data = new CategoryData
                    {
                        Id = id,
                        Name = cat.Name,
                        IsAnnotation = cat.CategoryType == CategoryType.Annotation
                    };
                    map.Add(id, data);
                }
                data.ElementIds.Add(e.Id);
            }

            return map.Values.OrderBy(c => c.IsAnnotation).ThenBy(c => c.Name).ToList();
        }

        /// <summary>Liste les paramètres d'occurrence et de type des catégories choisies.</summary>
        public static List<ParamDescriptor> CollectParameters(Document doc, IEnumerable<CategoryData> categories, bool byType)
        {
            var map = new Dictionary<string, ParamDescriptor>();

            foreach (CategoryData cat in categories)
            {
                int instanceCount = 0;
                var seenTypes = new HashSet<long>();

                foreach (ElementId id in cat.ElementIds)
                {
                    Element e = doc.GetElement(id);
                    if (e == null) continue;

                    if (!byType && instanceCount < SampleElementsPerCategory)
                    {
                        instanceCount++;
                        foreach (Parameter p in e.Parameters) Add(map, p, ParamOwner.Instance);
                    }

                    ElementId typeId = e.GetTypeId();
                    if (typeId != ElementId.InvalidElementId
                        && seenTypes.Count < SampleTypesPerCategory
                        && seenTypes.Add(typeId.Value))
                    {
                        Element type = doc.GetElement(typeId);
                        if (type != null)
                            foreach (Parameter p in type.Parameters) Add(map, p, ParamOwner.Type);
                    }

                    if ((byType || instanceCount >= SampleElementsPerCategory) && seenTypes.Count >= SampleTypesPerCategory)
                        break;
                }
            }

            return map.Values.OrderBy(d => d.Name).ThenBy(d => d.Owner).ToList();
        }

        private static void Add(Dictionary<string, ParamDescriptor> map, Parameter p, ParamOwner owner)
        {
            if (p?.Definition == null || p.StorageType == StorageType.None) return;

            string key = ParamKeys.KeyOf(p);
            string id = Owners.Code(owner) + "|" + key;

            if (map.TryGetValue(id, out ParamDescriptor existing))
            {
                // Lecture seule seulement si le paramètre l'est partout
                existing.IsReadOnly = existing.IsReadOnly && p.IsReadOnly;
                return;
            }

            map.Add(id, new ParamDescriptor
            {
                Key = key,
                Name = p.Definition.Name,
                Owner = owner,
                IsReadOnly = p.IsReadOnly,
                Storage = p.StorageType,
                DataType = ParamKeys.SafeDataType(p),
                Origin = ParamKeys.OriginOf(p)
            });
        }
    }
}
