using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace RevitExcelLink.Core
{
    /// <summary>Propose un nom de fichier lisible : « Projet – Contenu – 2026-11-05.xlsx ».</summary>
    public static class FileNames
    {
        private const int MaxListed = 3;
        private const int MaxLength = 120;

        public static string Suggest(Document doc, IList<string> contentNames, string pluralLabel)
        {
            string project = ProjectName(doc);
            string content;
            if (contentNames.Count == 0) content = "Export";
            else if (contentNames.Count <= MaxListed) content = string.Join(", ", contentNames);
            else content = $"{contentNames.Count} {pluralLabel}";

            string name = $"{project} – {content} – {DateTime.Now:yyyy-MM-dd}";
            name = Clean(name);
            if (name.Length > MaxLength)
                name = Clean($"{project} – {contentNames.Count} {pluralLabel} – {DateTime.Now:yyyy-MM-dd}");
            return name + ".xlsx";
        }

        /// <summary>Titre du modèle sans le suffixe « _utilisateur » des copies locales ni « détaché ».</summary>
        public static string ProjectName(Document doc)
        {
            string title = doc.Title ?? "";
            if (title.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) title = title.Substring(0, title.Length - 4);

            string user = Environment.UserName;
            if (!string.IsNullOrEmpty(user))
                title = Regex.Replace(title, "[_ -]" + Regex.Escape(user) + "$", "", RegexOptions.IgnoreCase);

            title = Regex.Replace(title, @"[_ -]?(détaché|detached|central|centrale)$", "", RegexOptions.IgnoreCase);
            title = title.Replace('_', ' ');
            title = Regex.Replace(title, @"\s{2,}", " ").Trim(' ', '-', '.');
            return title.Length > 0 ? title : "Projet";
        }

        private static string Clean(string name)
        {
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var sb = new StringBuilder();
            foreach (char c in name) sb.Append(invalid.Contains(c) ? ' ' : c);
            return Regex.Replace(sb.ToString(), @"\s{2,}", " ").Trim();
        }
    }
}
