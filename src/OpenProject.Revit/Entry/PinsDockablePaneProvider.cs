using Autodesk.Revit.UI;
using OpenProject.Revit.Views;

namespace OpenProject.Revit.Entry
{
  public sealed class PinsDockablePaneProvider : IDockablePaneProvider
  {
    private readonly PinsPaneView _view;

    public PinsDockablePaneProvider(PinsPaneView view)
    {
      _view = view;
    }

    public void SetupDockablePane(DockablePaneProviderData data)
    {
      data.FrameworkElement = _view;
      data.InitialState = new DockablePaneState
      {
        DockPosition = DockPosition.Right
      };
    }
  }
}
