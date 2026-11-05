using System;
using System.Reflection;
using Autodesk.Revit.UI;
using RevitExcelLink.Commands;
using RevitExcelLink.UI;

namespace RevitExcelLink
{
    /// <summary>Chargée au démarrage de Revit : crée l'onglet « Helora BIM » et affiche la page de garde.</summary>
    public class App : IExternalApplication
    {
        private const string TabName = "Helora BIM";
        private const string PanelName = "Excel Link";

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                try { application.CreateRibbonTab(TabName); }
                catch { /* l'onglet existe déjà */ }

                RibbonPanel panel = application.CreateRibbonPanel(TabName, PanelName);
                string assembly = Assembly.GetExecutingAssembly().Location;

                var export = new PushButtonData("RXL_Export", "Exporter\nvers Excel", assembly, typeof(ExportCommand).FullName)
                {
                    ToolTip = "Exporte vers Excel les paramètres des éléments ou des nomenclatures.",
                    LongDescription = "Choisissez la portée, les catégories et les paramètres. Le fichier contient une feuille par catégorie, "
                                      + "avec un code couleur (occurrence, type, lecture seule) et des listes déroulantes.",
                    LargeImage = Icons.Make(32, "↑", Icons.HeloraTurquoise),
                    Image = Icons.Make(16, "↑", Icons.HeloraTurquoise)
                };

                var import = new PushButtonData("RXL_Import", "Importer\ndepuis Excel", assembly, typeof(ImportCommand).FullName)
                {
                    ToolTip = "Réimporte un fichier Excel modifié et met à jour les paramètres dans Revit.",
                    LongDescription = "Seules les cellules modifiées depuis l'export sont prises en compte. "
                                      + "Un aperçu permet de vérifier chaque modification avant de l'appliquer.",
                    LargeImage = Icons.Make(32, "↓", Icons.HeloraDark),
                    Image = Icons.Make(16, "↓", Icons.HeloraDark)
                };

                panel.AddItem(export);
                panel.AddItem(import);

                // --- Groupe « Locaux »
                RibbonPanel rooms = application.CreateRibbonPanel(TabName, "Locaux");
                var numbering = new PushButtonData("HBIM_RoomNumbering", "Numéroter\nles locaux", assembly, typeof(RoomNumberingCommand).FullName)
                {
                    ToolTip = "Numérote les locaux un par un : BLOC/ÉTAGE/NUMÉRO (ex. B/N00/015).",
                    LongDescription = "Indiquez le bloc, l'étage et le numéro de départ, puis cliquez sur les locaux un par un. "
                                      + "Remplit le Numéro, HEL_BLOC, HEL_Etage et H_Num_GMAO. Échap permet de sauter des numéros ou de terminer.",
                    LargeImage = Icons.Make(32, "#", Icons.HeloraTurquoise),
                    Image = Icons.Make(16, "#", Icons.HeloraTurquoise)
                };
                rooms.AddItem(numbering);

                // --- Groupe « Helora BIM »
                RibbonPanel info = application.CreateRibbonPanel(TabName, "Helora BIM");

                var about = new PushButtonData("RXL_About", "À propos", assembly, typeof(AboutCommand).FullName)
                {
                    ToolTip = "Helora BIM – Excel Link : version et page de présentation.",
                    LargeImage = Icons.Make(32, "+", Icons.HeloraDark),
                    Image = Icons.Make(16, "+", Icons.HeloraDark)
                };
                info.AddItem(about);

                // Page de garde une fois Revit complètement chargé (animation fluide)
                application.ControlledApplication.ApplicationInitialized += (s, e) =>
                    SplashWindow.ShowFor(application.ControlledApplication.VersionNumber);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Helora BIM", "Impossible de créer le ruban : " + ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
    }
}
