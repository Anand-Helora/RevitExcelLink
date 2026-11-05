using System;
using System.Reflection;
using Autodesk.Revit.UI;
using RevitExcelLink.Commands;
using RevitExcelLink.UI;

namespace RevitExcelLink
{
    /// <summary>Chargée au démarrage de Revit : crée l'onglet « Excel Link » et ses deux boutons.</summary>
    public class App : IExternalApplication
    {
        private const string TabName = "Excel Link";

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                try { application.CreateRibbonTab(TabName); }
                catch { /* l'onglet existe déjà */ }

                RibbonPanel panel = application.CreateRibbonPanel(TabName, "Données");
                string assembly = Assembly.GetExecutingAssembly().Location;

                var export = new PushButtonData("RXL_Export", "Exporter\nvers Excel", assembly, typeof(ExportCommand).FullName)
                {
                    ToolTip = "Exporte les paramètres des éléments choisis vers un fichier Excel.",
                    LongDescription = "Choisissez la portée, les catégories et les paramètres. Le fichier contient une feuille par catégorie, "
                                      + "avec un code couleur (occurrence, type, lecture seule) et des listes déroulantes.",
                    LargeImage = Icons.Make(32, "↑", Icons.ExcelGreen),
                    Image = Icons.Make(16, "↑", Icons.ExcelGreen)
                };

                var import = new PushButtonData("RXL_Import", "Importer\ndepuis Excel", assembly, typeof(ImportCommand).FullName)
                {
                    ToolTip = "Réimporte un fichier Excel modifié et met à jour les paramètres dans Revit.",
                    LongDescription = "Seules les cellules modifiées depuis l'export sont prises en compte. "
                                      + "Un aperçu permet de vérifier chaque modification avant de l'appliquer.",
                    LargeImage = Icons.Make(32, "↓", Icons.ImportBlue),
                    Image = Icons.Make(16, "↓", Icons.ImportBlue)
                };

                panel.AddItem(export);
                panel.AddItem(import);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Excel Link", "Impossible de créer le ruban : " + ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
    }
}
