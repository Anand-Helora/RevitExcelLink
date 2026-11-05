using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RevitExcelLink.Core
{
    /// <summary>Sélection réutilisable (portée, catégories, paramètres et leur ordre), enregistrée en JSON.</summary>
    public sealed class ExportProfile
    {
        public string Scope { get; set; } = nameof(ExportScope.WholeModel);
        public bool ByType { get; set; }
        public List<long> CategoryIds { get; set; } = new List<long>();
        public List<string> CategoryNames { get; set; } = new List<string>();
        public List<string> Parameters { get; set; } = new List<string>(); // ParamDescriptor.Id, dans l'ordre

        public static string Folder
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                          "RevitExcelLink", "Profils");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public void Save(string path) =>
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

        public static ExportProfile Load(string path) =>
            JsonSerializer.Deserialize<ExportProfile>(File.ReadAllText(path)) ?? new ExportProfile();
    }
}
