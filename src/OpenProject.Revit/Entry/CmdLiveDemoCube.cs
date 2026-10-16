using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace OpenProject.Revit.Entry
{
  [Transaction(TransactionMode.Manual)]
  public sealed class CmdLiveDemoCube : IExternalCommand
  {
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
      var doc = data.Application.ActiveUIDocument?.Document;
      if (doc == null || doc.IsFamilyDocument)
      {
        message = "Abre un proyecto Revit para crear el cubo de prueba.";
        return Result.Failed;
      }
      const double side = 1 / 0.3048;
      var points = new[] { XYZ.Zero, new XYZ(side, 0, 0), new XYZ(side, side, 0), new XYZ(0, side, 0) };
      var loop = new CurveLoop();
      for (var i = 0; i < 4; i++) loop.Append(Line.CreateBound(points[i], points[(i + 1) % 4]));
      var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, side);
      using var transaction = new Transaction(doc, "Crear cubo demo de 1 m");
      transaction.Start();
      var cube = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
      cube.ApplicationId = "Cmcoordinator.LocalLiveSync";
      cube.Name = "Cubo demo · 1 m";
      cube.SetShape(new List<GeometryObject> { solid });
      transaction.Commit();
      data.Application.ActiveUIDocument.Selection.SetElementIds(new List<ElementId> { cube.Id });
      return Result.Succeeded;
    }
  }
}
