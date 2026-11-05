using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitExcelLink.Core
{
    /// <summary>
    /// Pour les paramètres qui pointent vers un autre élément (niveau, phase, matériau…) :
    /// liste les éléments d'une classe et retrouve un élément à partir de son nom.
    /// </summary>
    public sealed class ElementNameLookup
    {
        private readonly Document _doc;
        private readonly Dictionary<string, Dictionary<string, List<ElementId>>> _cache =
            new Dictionary<string, Dictionary<string, List<ElementId>>>();

        public ElementNameLookup(Document doc) { _doc = doc; }

        public IReadOnlyList<ElementId> Find(string className, string name)
        {
            var byName = Get(className);
            return byName.TryGetValue(name, out var ids) ? ids : (IReadOnlyList<ElementId>)Array.Empty<ElementId>();
        }

        /// <summary>Noms disponibles (triés) pour une liste déroulante.</summary>
        public List<string> Names(string className) => Get(className).Keys.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();

        private Dictionary<string, List<ElementId>> Get(string className)
        {
            if (_cache.TryGetValue(className, out var cached)) return cached;

            var result = new Dictionary<string, List<ElementId>>(StringComparer.Ordinal);
            Type type = typeof(Element).Assembly.GetType(className, false);
            if (type != null)
            {
                foreach (Element e in Collect(type))
                {
                    string n = e.Name;
                    if (string.IsNullOrEmpty(n)) continue;
                    if (!result.TryGetValue(n, out var list)) result[n] = list = new List<ElementId>();
                    list.Add(e.Id);
                }
            }
            _cache[className] = result;
            return result;
        }

        private IEnumerable<Element> Collect(Type type)
        {
            try
            {
                return new FilteredElementCollector(_doc).OfClass(type).ToElements();
            }
            catch
            {
                // Certaines classes ne sont pas acceptées par OfClass : on filtre à la main.
                var all = new FilteredElementCollector(_doc).WherePasses(
                    new LogicalOrFilter(new ElementIsElementTypeFilter(false), new ElementIsElementTypeFilter(true)));
                return all.Where(e => e.GetType() == type).ToList();
            }
        }
    }
}
