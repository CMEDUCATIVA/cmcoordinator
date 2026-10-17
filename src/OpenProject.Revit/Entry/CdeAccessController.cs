using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Newtonsoft.Json.Linq;
using OpenProject.Revit.LiveSync;
using OpenProject.Revit.Services;
using OpenProject.Shared.Authentication;
using Serilog;

namespace OpenProject.Revit.Entry
{
  internal static class CdeAccessController
  {
    private static CdeAccessState Access = new();
    private static bool _startup = true;
    private static bool _accountChanged;
    private static bool _signedOut;
    private static DateTime _nextShowAttempt;
    public static bool CanUseLocalSync => Access.HasLocalAccess;
    public static long SignedInUserId => Access.LastSignedInUserId;
    public static bool HasFreshCdeSession => RibbonButtonClickHandler.IsBrowserRunning && Access.HasFreshCdeSession(DateTime.UtcNow);
    public static void Initialize(UIControlledApplication application) => application.Idling += OnIdle;
    public static void Shutdown(UIControlledApplication application)
    { application.Idling -= OnIdle; Access = new CdeAccessState(); }
    public static void BeginBrowser(string nonce)
    { Access.BeginBrowser(nonce); }
    public static void Receive(string nonce, string payload)
    {
      try
      {
        var state = JObject.Parse(payload);
        if (!Enum.TryParse((string)state["sessionState"], out CdeSessionStatus status)) return;
        var previous = Access.LastSignedInUserId;
        var hadAccess = Access.HasLocalAccess;
        if (Access.Accept(nonce, (long)state["sequence"], new CdeSessionObservation(status, (long)state["userId"]),
            (DateTime)state["observedUtc"], DateTime.UtcNow))
        {
          if (previous > 0 && Access.LastSignedInUserId != previous) _accountChanged = true;
          if (hadAccess && !Access.HasLocalAccess) _signedOut = true;
        }
      }
      catch (Exception error) when (error is Newtonsoft.Json.JsonException || error is InvalidCastException || error is ArgumentException || error is OverflowException || error is FormatException)
      { Log.Warning("Estado de sesión CDE inválido; se conserva el control de acceso"); }
    }
    public static void Open(UIApplication app)
    {
      Access.RequestPanel();
      RibbonButtonClickHandler.OpenBrowser(app);
      if (CanUseLocalSync) Show(app);
    }
    private static void OnIdle(object sender, IdlingEventArgs args)
    {
      if (sender is not UIApplication app) return;
      try
      {
        if (_startup)
        {
          _startup = false;
          Open(app);
          if (!CanUseLocalSync) Hide(app);
        }
        if (_accountChanged) { LocalLiveSync.HoldForAccountChange(); _accountChanged = false; }
        if (_signedOut) { LocalLiveSync.HoldForSignIn(); _signedOut = false; }
        LocalLiveSync.SetCdeSessionStatus(ConnectionDescription());
        if (!CanUseLocalSync) Hide(app);
        if (CanUseLocalSync && Access.PanelRequested && DateTime.UtcNow >= _nextShowAttempt) Show(app);
      }
      catch (Exception error)
      { Log.Error(error, "No se pudo abrir Cmcoordinator; puede reintentarse desde su botón"); }
    }
    private static void Show(UIApplication app)
    {
      _nextShowAttempt = DateTime.UtcNow.AddSeconds(5);
      LocalLiveSync.Show(app);
      Access.PanelShown();
    }
    private static string ConnectionDescription()
    {
      if (Access.Status == CdeSessionStatus.SignedOut) return "Sesión CDE cerrada · sincronización en pausa";
      if (!CanUseLocalSync) return "Inicia sesión en el CDE para habilitar el centro";
      if (!RibbonButtonClickHandler.IsBrowserRunning) return "Trabajo local disponible · navegador CDE cerrado";
      return HasFreshCdeSession ? "Sesión CDE validada · trabajo local disponible"
        : "CDE pendiente de confirmar · trabajo local disponible";
    }
    private static void Hide(UIApplication app)
    {
      foreach (var id in new[] { LocalLiveSync.PaneId, PinsPaneManager.PaneId })
      {
        try { var pane = app.GetDockablePane(id); if (pane.IsShown()) pane.Hide(); }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException) { /* Pane unavailable on the home screen. */ }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { /* Revit has not created the pane yet. */ }
      }
    }
  }
}
