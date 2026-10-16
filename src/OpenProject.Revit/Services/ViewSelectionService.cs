using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenProject.Revit.Extensions;
using OpenProject.Shared.Math3D.Enumeration;

namespace OpenProject.Revit.Services
{
  public static class ViewSelectionService
  {
    public static ViewSelectionResult GetTargetView(UIApplication app, CameraType cameraType)
    {
      var activeView3D = app.ActiveUIDocument?.ActiveView as View3D;
      var canUseActiveView = IsActiveViewCompatible(activeView3D, cameraType);

      if (!canUseActiveView)
        return new ViewSelectionResult(app.ActiveUIDocument.Document.GetOpenProjectView(cameraType), applyClipping: false);

      var choice = AskUserForViewPreference(app);
      if (choice == null)
        return new ViewSelectionResult(activeView3D, applyClipping: false);

      var view = choice.Value.preference == ViewPreference.ActiveView
        ? activeView3D
        : app.ActiveUIDocument.Document.GetOpenProjectView(cameraType);
      return new ViewSelectionResult(view, choice.Value.applyClipping);
    }

    public static ViewSelectionResult GetTargetView(UIApplication app, CameraType cameraType, bool promptUser,
      bool applyClippingDefault)
    {
      var activeView3D = app.ActiveUIDocument?.ActiveView as View3D;
      var canUseActiveView = IsActiveViewCompatible(activeView3D, cameraType);

      if (!canUseActiveView)
        return new ViewSelectionResult(app.ActiveUIDocument.Document.GetOpenProjectView(cameraType),
          applyClipping: applyClippingDefault);

      if (!promptUser)
        return new ViewSelectionResult(activeView3D, applyClipping: applyClippingDefault);

      var choice = AskUserForViewPreference(app);
      if (choice == null)
        return new ViewSelectionResult(activeView3D, applyClipping: applyClippingDefault);

      var view = choice.Value.preference == ViewPreference.ActiveView
        ? activeView3D
        : app.ActiveUIDocument.Document.GetOpenProjectView(cameraType);
      return new ViewSelectionResult(view, choice.Value.applyClipping);
    }

    private static bool IsActiveViewCompatible(View3D activeView, CameraType cameraType)
    {
      if (activeView == null) return false;
      return cameraType == CameraType.Perspective
        ? activeView.IsPerspective
        : !activeView.IsPerspective;
    }

    private static (ViewPreference preference, bool applyClipping)? AskUserForViewPreference(UIApplication app)
    {
      var dialog = new TaskDialog("BCF View")
      {
        TitleAutoPrefix = true,
        MainInstruction = "¿Qué vista deseas usar para abrir el BCF?",
        MainContent =
          "Usar la vista activa modificará su cámara, visibilidad y sección para coincidir con el BCF."
      };

      dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Usar vista activa (recomendada)");
      dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Usar vista Cmcoordinator");
      dialog.VerificationText = "Aplicar clipping (caja de sección)";

      var result = dialog.Show();

      return result switch
      {
        TaskDialogResult.CommandLink1 => (ViewPreference.ActiveView, dialog.WasVerificationChecked()),
        TaskDialogResult.CommandLink2 => (ViewPreference.CmcoordinatorView, dialog.WasVerificationChecked()),
        _ => null
      };
    }
  }

  public readonly struct ViewSelectionResult
  {
    public ViewSelectionResult(View3D view, bool applyClipping)
    {
      View = view;
      ApplyClipping = applyClipping;
    }

    public View3D View { get; }
    public bool ApplyClipping { get; }
  }
}
