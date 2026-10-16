using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenProject.Revit.LiveSync;

namespace OpenProject.Revit.Entry
{
  [Transaction(TransactionMode.Manual)]
  public sealed class CmdLocalLiveSync : IExternalCommand
  {
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
      try
      {
        LocalLiveSync.Start(data.Application);
        return Result.Succeeded;
      }
      catch (Exception exception)
      {
        message = exception.Message;
        return Result.Failed;
      }
    }
  }
}
