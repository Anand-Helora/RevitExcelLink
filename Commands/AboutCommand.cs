using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitExcelLink.UI;

namespace RevitExcelLink.Commands
{
    /// <summary>Réaffiche la page de garde Helora BIM.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class AboutCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            SplashWindow.ShowFor(commandData.Application.Application.VersionNumber);
            return Result.Succeeded;
        }
    }
}
