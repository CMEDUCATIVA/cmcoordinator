using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using OpenProject.Revit.LiveSync.Presentation;
using OpenProject.Revit.LiveSync.Queue;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  public class SyncPanelBindingTests
  {
    [Fact]
    public void ProductionPanelLoadsAndUpdatesReadOnlyPresentationProperties()
    {
      RunSta(() =>
      {
        // Exercise the actual production markup without calling Revit or button handlers.
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("LiveSync.SyncPanelView.xaml");
        using var reader = new StreamReader(resource);
        var markup = XDocument.Parse(reader.ReadToEnd());
        // The activity log has its own WPF component; this test isolates the parent bindings.
        markup.Descendants().First(e => e.Name.LocalName == "SyncHistoryView").Remove();
        markup.Root.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Remove();
        foreach (var attribute in markup.Descendants().Attributes().Where(a =>
          a.Name.LocalName is "Click" or "TextChanged" or "PasswordChanged").ToArray()) attribute.Remove();
        var panel = (UserControl)XamlReader.Parse(markup.ToString());
        var model = new SyncPanelViewModel();
        panel.DataContext = model;
        Layout(panel);
        var progress = Descendants(panel).OfType<ProgressBar>().Single();
        Assert.Equal(0d, progress.Value);
        Assert.Equal(Visibility.Collapsed, progress.Visibility);
        Assert.Equal(BindingMode.OneWay, BindingOperations.GetBinding(progress, ProgressBar.ValueProperty).Mode);

        model.Refresh("Modelo de prueba", "Capturando", new SyncProgress
        {
          Stage = "Sincronizando", JobId = "job-1", Done = 2, Total = 4, Paused = true,
          ReceiverRevision = "1234567890", ViewerRevision = "abcdefghij"
        }, "Diagnóstico local", capturedElements: 907, hasSyncWorker: true);
        Layout(panel);
        Assert.Equal(50d, progress.Value);
        Assert.Equal(0.5d, model.TransferFraction);
        Assert.Equal(Visibility.Visible, progress.Visibility);
        Assert.Equal(model.BlockLabel, System.Windows.Automation.AutomationProperties.GetName(progress));
        Assert.Contains(Descendants(panel).OfType<TextBox>(), b => b.Text == "2 de 4 paquetes · revisión pendiente" && b.IsReadOnly);
        Assert.Contains(Descendants(panel).OfType<TextBlock>(), b => b.Text == "Reanudar");
        Assert.Contains(Descendants(panel).OfType<TextBox>(), b => b.Text == "907" && b.IsReadOnly);
        Assert.Equal("Pausado", model.TransferTitle);
        Assert.Contains(Descendants(panel).OfType<TextBox>(), b => b.Text == "Modelo de prueba" && b.IsReadOnly);
        Assert.Contains(Descendants(panel).OfType<TextBox>(), b => b.Text == "Revit inactivo" && b.IsReadOnly);
        model.SetDestination("1-CM", "Estructura prueba");
        model.SetConnectionState(CdeConnectionDisplayState.Validated);
        model.Refresh("Modelo de prueba", "Capturando", new SyncProgress { ReceiverRevision = "1234567890", ViewerRevision = "abcdefghij" }, "Diagnóstico local",
          documentTitle: "Estructura prueba", revitActive: true, capturedElements: 907, hasSyncWorker: true);
        Layout(panel);
        Assert.Contains(Descendants(panel).OfType<TextBox>(), b => b.Text == "Estructura prueba" && b.IsReadOnly);
        Assert.Contains(Descendants(panel).OfType<TextBox>(), b => b.Text == "Revit activo" && b.IsReadOnly);
        Assert.Contains(Descendants(panel).OfType<TextBlock>(), b => b.Text == "Conexión validada");
        Assert.Contains(Descendants(panel).OfType<TextBox>(), b => b.Text == "CENTRO DE CMCOORDINATOR" && b.IsReadOnly);
        var selectableTitle = Descendants(panel).OfType<TextBox>().First(b => b.Text == "Estructura prueba" && b.IsReadOnly);
        selectableTitle.Select(0, selectableTitle.Text.Length);
        Assert.Equal("Estructura prueba", selectableTitle.SelectedText);
        Assert.Equal(Brushes.DarkGray, model.SyncIndicatorBrush);
        Assert.Equal(Visibility.Collapsed, progress.Visibility);
        Assert.DoesNotContain(Descendants(panel).OfType<TextBlock>(), b => b.Text is "Visor local" or "Reintentar" or "Abrir visor" or "Captura al día · 907 elementos" or "Guardado · visor pendiente");
        panel.Measure(new Size(280, 900));
        Assert.True(panel.DesiredSize.Width <= 280, "El panel superior debe caber en el ancho mínimo");
        var panelPreview = Environment.GetEnvironmentVariable("CMCOORDINATOR_PANEL_PREVIEW");
        if (!string.IsNullOrEmpty(panelPreview))
        {
          panel.Arrange(new Rect(0, 0, 280, 900)); panel.UpdateLayout();
          var bitmap = new RenderTargetBitmap(280, 900, 96, 96, PixelFormats.Pbgra32);
          bitmap.Render(panel);
          var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
          using var output = File.Create(panelPreview); png.Save(output);
        }
        Assert.DoesNotContain(Descendants(panel).OfType<TextBox>(), b => b.Text is "Última revisión guardada" or "Última revisión aplicada" or "Cola local persistente. Puedes cerrar este panel y seguir modelando. Los logs permanecen en este equipo.");
        var download = Descendants(panel).OfType<Button>().Single(b => (string)b.Tag == "export");
        Assert.Equal("Descargar diagnóstico", Assert.IsType<TextBlock>(download.Content).Text);
        var restart = Descendants(panel).OfType<Button>().Single(b => (string)b.Tag == "reset");
        Assert.Equal("Reiniciar captura", Assert.IsType<TextBlock>(restart.Content).Text);
        Assert.True(restart.IsEnabled);
        Assert.DoesNotContain(Descendants(panel).OfType<Button>(), b => (string)b.Tag == "stop");
        Assert.Contains(Descendants(panel).OfType<TextBlock>(), t => t.Text.Contains("logs en un ZIP"));
        Assert.Contains(Descendants(panel).OfType<TextBlock>(), t => t.Text.Contains("leer el modelo completo"));

        var expander = Descendants(panel).OfType<Expander>().Single(e => (string)e.Header == "Actividad y diagnóstico");
        expander.IsExpanded = true;
        Layout(panel);
        Assert.Contains(Descendants(panel).OfType<TextBox>(), t => t.IsReadOnly && t.Text == "Diagnóstico local");
        Assert.DoesNotContain(Descendants(expander).OfType<Button>(), b => (string)b.Tag == "reset");
        Assert.DoesNotContain(Descendants(expander).OfType<Button>(), b => (string)b.Tag == "export");
        var activityBox = Descendants(expander).OfType<TextBox>().Single(t => t.IsReadOnly && t.Text == "Diagnóstico local");
        Assert.Equal(240, activityBox.Height);
        Assert.Equal(ScrollBarVisibility.Visible, activityBox.VerticalScrollBarVisibility);
        var longLog = string.Join(Environment.NewLine, Enumerable.Range(0, 80).Select(i => $"Evento {i}: revisión guardada en el CDE"));
        model.Refresh("Modelo de prueba", "Listo", new SyncProgress { Stage = "Al día" }, longLog,
          capturedElements: 907, cdeConnected: true, hasSyncWorker: true);
        Layout(panel);
        activityBox.Focus(); activityBox.ScrollToEnd(); activityBox.UpdateLayout(); PumpDispatcher(50);
        Assert.True(activityBox.VerticalOffset > 0,
          $"El diagnóstico debe desplazarse: texto={activityBox.Text.Length}, extensión={activityBox.ExtentHeight}, vista={activityBox.ViewportHeight}, offset={activityBox.VerticalOffset}");
        var diagnosticPreview = Environment.GetEnvironmentVariable("CMCOORDINATOR_DIAGNOSTIC_PREVIEW");
        if (!string.IsNullOrEmpty(diagnosticPreview))
        {
          panel.Measure(new Size(280, 900)); panel.Arrange(new Rect(0, 0, 280, 900)); panel.UpdateLayout();
          var bitmap = new RenderTargetBitmap(280, 900, 96, 96, PixelFormats.Pbgra32);
          bitmap.Render(panel);
          var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
          using var output = File.Create(diagnosticPreview); png.Save(output);
        }
        model.Refresh("Modelo de prueba", "Listo", new SyncProgress { Stage = "Al día" }, "Finalizado",
          capturedElements: 907, cdeConnected: true, hasSyncWorker: true);
        Layout(panel);
        Assert.Equal("Listo para sincronizar", model.TransferTitle);
        Assert.Equal(Brushes.DarkGray, model.SyncIndicatorBrush);
        Assert.True(model.CanSynchronize);
        model.Refresh("Modelo de prueba", "Preparando", new SyncProgress { Stage = "Preparando paquetes" }, "Finalizado",
          capturedElements: 907, cdeConnected: true, hasSyncWorker: true, syncRequested: true);
        Assert.False(model.CanSynchronize);
        Assert.Equal("Preparando…", model.SyncLabel);
        model.Refresh("Modelo de prueba", "Preparando", new SyncProgress
        {
          Stage = "Preparando paquetes", Done = 2740, Total = 5481, BytesDone = 42 * 1024 * 1024
        }, "Finalizado", capturedElements: 5481, cdeConnected: true, hasSyncWorker: true,
          revitActive: true, revitProcessing: true, syncPreparing: true);
        Layout(panel);
        Assert.Equal(49, model.TransferPercent);
        Assert.False(model.TransferIndeterminate);
        Assert.Contains("2.740 de 5.481 elementos · 49 %", model.BlockLabel);
        Assert.False(model.CanSynchronize);
        // Capture only runs after Sincronizar; while it runs the button stays blocked, even under a reconciliation hold.
        model.Refresh("Modelo de prueba", "Preparando geometría", new SyncProgress { Stage = "En pausa", Paused = true }, "Finalizado",
          capturedElements: 5481, cdeConnected: true, hasSyncWorker: true, revitActive: true,
          revitProcessing: true, syncPreparing: true, captureDone: 1370, captureTotal: 5481);
        Layout(panel);
        Assert.Equal(24, model.TransferPercent);
        Assert.Contains("1.370 de 5.481 unidades del modelo · 24 %", model.BlockLabel);
        Assert.Equal("Preparando cambios", model.TransferTitle);
        Assert.Equal("Pausar", model.PauseLabel);
        Assert.False(model.CanSynchronize);
        Assert.Equal("Preparando…", model.SyncLabel);
        // Connecting alone leaves the worker waiting for the first capture and Sincronizar available.
        model.Refresh("Modelo de prueba", "Conectado", new SyncProgress { Stage = "Esperando captura" }, "Finalizado",
          cdeConnected: true, hasSyncWorker: true, revitActive: true);
        Layout(panel);
        Assert.Equal("Listo para sincronizar", model.TransferTitle);
        Assert.Equal("Pulsa Sincronizar para capturar y publicar la vista 3D", model.TransferDetail);
        Assert.True(model.CanSynchronize);
        Assert.Equal("Sincronizar", model.SyncLabel);
        model.Refresh("Modelo de prueba", "Enviando", new SyncProgress { Stage = "Enviando", JobId = "job-3", Done = 1, Total = 4 }, "Finalizado",
          capturedElements: 907, cdeConnected: true, hasSyncWorker: true, revitActive: true);
        Layout(panel);
        Assert.Equal("Sincronizando", model.TransferTitle);
        Assert.Equal(Brushes.DodgerBlue, model.SyncIndicatorBrush);
        model.Refresh("Modelo de prueba", "Listo", new SyncProgress { Stage = "Al día", ReceiverRevision = "rev-1", ViewerRevision = "rev-0" }, "Finalizado",
          capturedElements: 907, cdeConnected: true, hasSyncWorker: true);
        Layout(panel);
        // Stored by the CDE is synchronised even if no viewer has shown it yet.
        Assert.Equal("Sincronizado", model.TransferTitle);
        Assert.Equal(Brushes.LimeGreen, model.SyncIndicatorBrush);
        Assert.Equal("Revisión guardada en el CDE", model.TransferDetail);
        model.Refresh("Modelo de prueba", "Listo", new SyncProgress { Stage = "Al día", ReceiverRevision = "rev-1", ViewerRevision = "rev-1" }, "Finalizado",
          capturedElements: 907, cdeConnected: true, hasSyncWorker: true);
        Layout(panel);
        Assert.Equal(0d, progress.Value);
        Assert.Equal(0d, model.TransferFraction);
        Assert.Equal(Visibility.Collapsed, progress.Visibility);
        Assert.Equal("Sincronizado", model.TransferTitle);
        Assert.Equal(Brushes.LimeGreen, model.SyncIndicatorBrush);
        Assert.Equal("Estado de sincronización: Sincronizado", model.SyncIndicatorLabel);
        Assert.Equal("Revisión guardada en el CDE y mostrada en el visor", model.TransferDetail);
        Assert.Equal("", model.TransferPercentLabel);
        Assert.Contains(Descendants(panel).OfType<TextBox>(), b => b.Text == "Revisión confirmada en CDE" && b.IsReadOnly);
        model.Refresh("Modelo de prueba", "Rechazado", new SyncProgress { Stage = "Requiere atención", JobId = "job-2", Done = 4, Total = 4, Attention = true }, "Finalizado",
          capturedElements: 907, cdeConnected: true, hasSyncWorker: true);
        Layout(panel);
        Assert.Equal("Error", model.TransferTitle);
        Assert.True(model.CanSynchronize);
        Assert.Equal("Sincronizar", model.SyncLabel);
        Assert.Equal(Visibility.Visible, progress.Visibility);
        Assert.Contains(Descendants(panel).OfType<TextBox>(), b => b.Text == "4 de 4 paquetes · revisión no aceptada" && b.IsReadOnly);
        Assert.Contains(Descendants(panel).OfType<TextBox>(), t => t.IsReadOnly && t.Text == "Finalizado");
        model.Refresh("Modelo de prueba", "Esperando", new SyncProgress { Stage = "En espera de publicación", Detail = "Otra conexión está publicando", JobId = "job-4", Total = 4, Attention = true }, "Finalizado",
          capturedElements: 907, cdeConnected: true, hasSyncWorker: true);
        model.SetConnectionState(CdeConnectionDisplayState.Waiting);
        Layout(panel);
        Assert.Equal("Esperando publicación", model.TransferTitle);
        Assert.Equal("Esperando publicación", model.ConnectionState);
        Assert.Equal(Brushes.Goldenrod, model.TransferBrush);
        Assert.Equal("0 de 4 paquetes · esperando turno de publicación", model.BlockLabel);
        Assert.Equal(CdeConnectionDisplayState.Waiting, SyncPanelViewModel.ResolveConnectionState(true, model.Status, false, true));
        Assert.Equal(CdeConnectionDisplayState.Error, SyncPanelViewModel.ResolveConnectionState(true, new SyncProgress { Stage = "Requiere atención", Attention = true }, false, true));
        model.Refresh("Modelo de prueba", "Detenido", new SyncProgress { Stage = "En pausa", Paused = true }, "Finalizado",
          capturedElements: 907, captureStopped: true, cdeConnected: true, hasSyncWorker: true);
        Layout(panel);
        Assert.Equal("Detenido", model.TransferTitle);
        Assert.False(model.CanStop);
        Assert.Equal("Reanudar", model.PauseLabel);
        panel.DataContext = null;
      });
    }
    [Fact]
    public void ModelGroupDrivesTheSyncButtonNoticeAndReplaceAction()
    {
      RunSta(() =>
      {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("LiveSync.SyncPanelView.xaml");
        using var reader = new StreamReader(resource);
        var markup = XDocument.Parse(reader.ReadToEnd());
        markup.Descendants().First(e => e.Name.LocalName == "SyncHistoryView").Remove();
        markup.Root.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Remove();
        foreach (var attribute in markup.Descendants().Attributes().Where(a =>
          a.Name.LocalName is "Click" or "TextChanged" or "PasswordChanged").ToArray()) attribute.Remove();
        var panel = (UserControl)XamlReader.Parse(markup.ToString());
        var model = new SyncPanelViewModel();
        panel.DataContext = model;
        model.SetDestination("1-CM", "Arquitectura");
        Layout(panel);
        var sync = Descendants(panel).OfType<Button>().Single(b => (string)b.Tag == "sync");
        var replace = Descendants(panel).OfType<Button>().Single(b => (string)b.Tag == "replace");
        var preview = Environment.GetEnvironmentVariable("CMCOORDINATOR_ACCESS_PREVIEW");
        void Show(string level, string[] actions, bool exists, string name)
        {
          var access = OpenProject.Revit.LiveSync.Transport.CdeModelAccess.From(new OpenProject.Revit.LiveSync.Transport.ReceiverStatus
            { AccessLevel = level, AllowedActions = actions, ModelExists = exists });
          model.Refresh("Modelo de prueba", "Listo", new SyncProgress { Stage = "Al día" }, "Diagnóstico", capturedElements: 907,
            cdeConnected: true, hasSyncWorker: true, access: access);
          model.SetConnectionState(CdeConnectionDisplayState.Connected);
          Layout(panel);
          panel.Measure(new Size(280, 1400));
          Assert.True(panel.DesiredSize.Width <= 280, "El panel con aviso debe caber en el ancho mínimo");
          if (string.IsNullOrEmpty(preview)) return;
          Directory.CreateDirectory(preview);
          panel.Arrange(new Rect(0, 0, 280, 1400)); panel.UpdateLayout();
          var bitmap = new RenderTargetBitmap(280, 760, 96, 96, PixelFormats.Pbgra32);
          bitmap.Render(panel);
          var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
          using var output = File.Create(Path.Combine(preview, name + ".png")); png.Save(output);
        }

        Show("reader", Array.Empty<string>(), true, "lectura");
        Assert.Equal("Solo lectura", model.SyncLabel);
        Assert.False(sync.IsEnabled);
        Assert.Equal("Solo lectura", AutomationProperties.GetName(sync));
        Assert.Equal(PanelNoticeKind.Warning, model.NoticeKind);
        Assert.Contains(Descendants(panel).OfType<TextBox>(), b => b.Text == "Solo lectura en este modelo");
        Assert.Equal(Visibility.Visible, model.NoticeVisibility);
        Assert.Equal("Lectura", model.GroupLabel);
        Assert.Equal(Visibility.Visible, model.GroupVisibility);
        Assert.Equal("Solo lectura", model.TransferTitle);
        Assert.DoesNotContain("Pulsa Sincronizar", model.TransferDetail);
        Assert.Equal(Visibility.Collapsed, replace.Visibility);

        Show("admin", new[] { "create" }, false, "crear");
        Assert.Equal("Crear modelo", model.SyncLabel);
        Assert.True(sync.IsEnabled);
        Assert.Equal(PanelNoticeKind.Info, model.NoticeKind);
        Assert.Contains(Descendants(panel).OfType<TextBox>(), b => b.Text == "El modelo está vacío");
        Assert.StartsWith("Pulsa Crear modelo", model.TransferDetail);
        Assert.Equal(Visibility.Visible, model.NoticeVisibility);
        Assert.Equal(Visibility.Collapsed, replace.Visibility);

        Show("admin", new[] { "sync", "replace" }, true, "administracion");
        Assert.Equal("Sincronizar", model.SyncLabel);
        Assert.True(sync.IsEnabled);
        // Coming from Lectura, the change of group is announced until the first synchronization.
        Assert.Equal(PanelNoticeKind.Success, model.NoticeKind);
        Assert.Equal("Ahora eres Administración", model.NoticeTitle);
        Assert.Equal(Visibility.Visible, replace.Visibility);
        Assert.True(replace.IsEnabled);
        Assert.Equal("Administración", model.GroupLabel);
        Assert.Equal("Listo para sincronizar", model.TransferTitle);

        Show("editor", new[] { "sync" }, true, "editor");
        Assert.Equal("Sincronizar", model.SyncLabel);
        Assert.Equal(Visibility.Collapsed, replace.Visibility);

        // A CDE without per-model groups keeps the previous behaviour.
        Show(null, null, true, "sin-grupos");
        Assert.Equal("Sincronizar", model.SyncLabel);
        Assert.True(sync.IsEnabled);
        Assert.Equal(PanelNoticeKind.None, model.NoticeKind);
        Assert.Equal(Visibility.Collapsed, model.GroupVisibility);
        Assert.Contains(Descendants(panel).OfType<TextBlock>(), b => b.Text == "Conectado");
      });
    }
    [Fact]
    public void RevocationPromotionQueueAndRestoreFollowUi8()
    {
      RunSta(() =>
      {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("LiveSync.SyncPanelView.xaml");
        using var reader = new StreamReader(resource);
        var markup = XDocument.Parse(reader.ReadToEnd());
        markup.Descendants().First(e => e.Name.LocalName == "SyncHistoryView").Remove();
        markup.Root.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Remove();
        foreach (var attribute in markup.Descendants().Attributes().Where(a =>
          a.Name.LocalName is "Click" or "TextChanged" or "PasswordChanged").ToArray()) attribute.Remove();
        var panel = (UserControl)XamlReader.Parse(markup.ToString());
        var model = new SyncPanelViewModel();
        panel.DataContext = model;
        model.SetDestination("1-CM", "Arquitectura");
        Layout(panel);
        var sync = Descendants(panel).OfType<Button>().Single(b => (string)b.Tag == "sync");
        var replace = Descendants(panel).OfType<Button>().Single(b => (string)b.Tag == "replace");
        var restore = Descendants(panel).OfType<Button>().Single(b => (string)b.Tag == "restore");
        void Show(string level, string[] actions, SyncProgress progress, bool syncRequested = false)
        {
          var access = OpenProject.Revit.LiveSync.Transport.CdeModelAccess.From(new OpenProject.Revit.LiveSync.Transport.ReceiverStatus
            { AccessLevel = level, AllowedActions = actions, ModelExists = true });
          model.Refresh("Modelo de prueba", "Listo", progress, "Diagnóstico", capturedElements: 907,
            cdeConnected: true, hasSyncWorker: true, syncRequested: syncRequested, access: access);
          Layout(panel);
          panel.Measure(new Size(280, 1400));
          Assert.True(panel.DesiredSize.Width <= 280, "El panel debe caber en el ancho mínimo");
          var preview = Environment.GetEnvironmentVariable("CMCOORDINATOR_ACCESS_PREVIEW");
          if (string.IsNullOrEmpty(preview)) return;
          Directory.CreateDirectory(preview);
          panel.Arrange(new Rect(0, 0, 280, 1400)); panel.UpdateLayout();
          var bitmap = new RenderTargetBitmap(280, 820, 96, 96, PixelFormats.Pbgra32);
          bitmap.Render(panel);
          var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
          using var output = File.Create(Path.Combine(preview, "ui8-" + progress.Stage.Replace(" ", "-") + "-" + level + ".png")); png.Save(output);
        }

        // UI-8 #2 · Lectura → Editor: green notice until the first synchronization.
        Show("reader", Array.Empty<string>(), new SyncProgress { Stage = "Al día", HasPendingChanges = true });
        Assert.Contains("guardados en este equipo", model.NoticeText);
        Show("editor", new[] { "sync" }, new SyncProgress { Stage = "Al día", HasPendingChanges = true });
        Assert.Equal(PanelNoticeKind.Success, model.NoticeKind);
        Assert.Contains(Descendants(panel).OfType<TextBox>(), b => b.Text == "Ahora eres Editor");
        Assert.Contains("pulsa Sincronizar", model.NoticeText);
        Show("editor", new[] { "sync" }, new SyncProgress { Stage = "Al día" }, syncRequested: true);
        Assert.Equal(PanelNoticeKind.None, model.NoticeKind);

        // UI-8 #10 · position in the CDE line.
        Show("editor", new[] { "sync" }, new SyncProgress { Stage = "En cola del CDE", QueuePosition = 2, JobId = "j", Total = 1 });
        Assert.Equal("En cola · posición 2", model.TransferTitle);

        // UI-8 #11 · revoked: red notice and «Volver a conectar», which stays enabled.
        var revoked = new SyncProgress { Stage = "Conexión revocada", Attention = true, Revoked = true };
        Show("admin", new[] { "sync", "replace", "restore" }, revoked);
        Assert.Equal("Volver a conectar", model.SyncLabel);
        Assert.True(sync.IsEnabled);
        Assert.Equal(PanelNoticeKind.Danger, model.NoticeKind);
        Assert.Contains(Descendants(panel).OfType<TextBox>(), b => b.Text == "Conexión revocada");
        Assert.Equal("Conexión revocada", model.TransferTitle);
        Assert.Equal(Visibility.Collapsed, replace.Visibility);
        Assert.Equal(Visibility.Collapsed, restore.Visibility);
        Assert.Equal(CdeConnectionDisplayState.Revoked, SyncPanelViewModel.ResolveConnectionState(true, revoked, false, true));

        // UI-8 #9 · after a replacement Administración can restore the model it replaced.
        Show("admin", new[] { "sync", "replace", "restore" }, new SyncProgress { Stage = "Al día" });
        Assert.Equal(Visibility.Visible, restore.Visibility);
        Assert.True(restore.IsEnabled);
        Show("admin", new[] { "sync", "replace" }, new SyncProgress { Stage = "Al día" });
        Assert.Equal(Visibility.Collapsed, restore.Visibility);
      });
    }
    [Fact]
    public void LiveButtonComparisonAndIdentityFollowUi8Proposal()
    {
      RunSta(() =>
      {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("LiveSync.SyncPanelView.xaml");
        using var reader = new StreamReader(resource);
        var markup = XDocument.Parse(reader.ReadToEnd());
        markup.Descendants().First(e => e.Name.LocalName == "SyncHistoryView").Remove();
        markup.Root.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Remove();
        foreach (var attribute in markup.Descendants().Attributes().Where(a =>
          a.Name.LocalName is "Click" or "TextChanged" or "PasswordChanged").ToArray()) attribute.Remove();
        var panel = (UserControl)XamlReader.Parse(markup.ToString());
        var model = new SyncPanelViewModel();
        panel.DataContext = model;
        model.SetDestination("cmeducativa", "Arquitectura");
        Layout(panel);
        var sync = Descendants(panel).OfType<Button>().Single(b => (string)b.Tag == "sync");
        var replace = Descendants(panel).OfType<Button>().Single(b => (string)b.Tag == "replace");
        var preview = Environment.GetEnvironmentVariable("CMCOORDINATOR_ACCESS_PREVIEW");
        void Show(SyncProgress progress, string name, bool live = false, bool chosen = false, string level = "admin")
        {
          var access = OpenProject.Revit.LiveSync.Transport.CdeModelAccess.From(new OpenProject.Revit.LiveSync.Transport.ReceiverStatus
            { AccessLevel = level, AllowedActions = level == "admin" ? new[] { "sync", "replace" } : new[] { "sync" }, ModelExists = true, UserName = "Francis Vin" });
          model.Refresh("Modelo de prueba", "Listo", progress, "Diagnóstico", capturedElements: 907,
            cdeConnected: true, hasSyncWorker: true, access: access, liveMode: live, syncChosen: chosen);
          Layout(panel);
          panel.Measure(new Size(280, 1400));
          Assert.True(panel.DesiredSize.Width <= 280, "El panel debe caber en el ancho mínimo");
          if (string.IsNullOrEmpty(preview)) return;
          Directory.CreateDirectory(preview);
          panel.Arrange(new Rect(0, 0, 280, 1400)); panel.UpdateLayout();
          var bitmap = new RenderTargetBitmap(280, 820, 96, 96, PixelFormats.Pbgra32);
          bitmap.Render(panel);
          var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
          using var output = File.Create(Path.Combine(preview, "ui8v66-" + name + ".png")); png.Save(output);
        }

        // UI-8 #27 · who is connected, with the group.
        Show(new SyncProgress { Stage = "Al día" }, "cabecera");
        Assert.Equal("Francis Vin", model.IdentityName);
        var identity = Descendants(panel).OfType<TextBlock>().Select(b => b.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text).ToArray())
          .FirstOrDefault(runs => runs.Contains("Francis Vin"));
        Assert.NotNull(identity);
        Assert.Contains("Administración", identity);

        // UI-8 #24 · connecting compares and says so before anything is sent.
        var differs = new ReplacePreview { RemoteCount = 12465, Compared = true, Exact = true, Local = 12430, Added = 35, Changed = 120, Removed = 4 };
        Show(new SyncProgress { Stage = "Al día", Comparison = differs }, "compara-difiere");
        Assert.Equal(PanelNoticeKind.Info, model.NoticeKind);
        Assert.Equal("Tu Revit difiere del CDE", model.NoticeTitle);
        Assert.Contains("+35 nuevos · 120 cambiados · −4 que no tienes", model.NoticeText);
        Assert.Contains("Reemplazar quita el modelo", model.NoticeText);
        Assert.Equal("Sincronizar", model.SyncLabel);
        Assert.True(replace.IsEnabled);
        var matches = new ReplacePreview { RemoteCount = 12430, Compared = true, Exact = true, Local = 12430 };
        Show(new SyncProgress { Stage = "Al día", Comparison = matches }, "compara-coincide", level: "editor");
        Assert.Equal("Coincide con el CDE", model.NoticeTitle);
        Assert.DoesNotContain("Reemplazar", model.NoticeText);
        Show(new SyncProgress { Stage = "Al día", Comparison = new ReplacePreview { RemoteCount = 900 } }, "compara-sin-captura");
        Assert.Equal("Modelo con contenido en el CDE", model.NoticeTitle);

        // UI-8 #25 · after choosing Sincronizar, «Reemplazar» stays visible, off, with the reason.
        Show(new SyncProgress { Stage = "Solicitud de sincronización", Comparison = differs }, "reemplazar-gris", live: true, chosen: true);
        Assert.Equal(Visibility.Visible, replace.Visibility);
        Assert.False(replace.IsEnabled);
        Assert.StartsWith("Ya estás unido al modelo", model.ReplaceHint);
        Assert.Equal(PanelNoticeKind.None, model.NoticeKind); // The comparison notice goes once the person chose.

        // UI-8 #26 · synchronized and live: pressed «En vivo», click ignored, full colour.
        var stored = new SyncProgress { Stage = "Al día", Joined = true, ReceiverRevision = "rev-1", ViewerRevision = "rev-1" };
        Show(stored, "en-vivo", live: true, chosen: true);
        Assert.True(model.IsLive);
        Assert.Equal("En vivo", model.SyncLabel);
        Assert.Equal("En vivo", AutomationProperties.GetName(sync));
        Assert.False(model.CanSynchronize);
        Assert.True(sync.IsEnabled);
        Assert.Equal(new Thickness(3, 3, 1, 1), sync.BorderThickness);
        Assert.Equal("Sincronizado", model.TransferTitle);
        // A live edit being sent keeps «En vivo».
        Show(new SyncProgress { Stage = "Enviando", Joined = true, JobId = "j", Total = 2, Done = 1 }, "en-vivo-enviando", live: true, chosen: true);
        Assert.Equal("En vivo", model.SyncLabel);

        // UI-8 #11 · a lost connection, a pause or an error bring Sincronizar back to verify.
        Show(new SyncProgress { Stage = "Esperando conexión", Joined = true, HasPendingChanges = true }, "sin-red", live: true, chosen: true);
        Assert.False(model.IsLive);
        Assert.Equal("Sincronizar", model.SyncLabel);
        Assert.True(model.CanSynchronize);
        Show(new SyncProgress { Stage = "En pausa", Joined = true, Paused = true }, "pausa", live: false, chosen: true);
        Assert.False(model.IsLive);

        // UI-8 #29 · a first merge that differs in nearly everything is confirmed first.
        var mostly = new ReplacePreview { RemoteCount = 12000, Compared = true, Exact = true, Local = 12000, Changed = 11900 };
        Show(new SyncProgress { Stage = SyncWorker.ConfirmStage, NeedsConfirmation = true, Comparison = mostly }, "confirmar", live: true, chosen: true);
        Assert.Equal(PanelNoticeKind.Warning, model.NoticeKind);
        Assert.Equal("Tu Revit difiere del CDE en casi todo", model.NoticeTitle);
        Assert.Equal("Confirmar sincronización…", model.SyncLabel);
        Assert.True(sync.IsEnabled);
        Assert.Equal(SyncWorker.ConfirmStage, model.TransferTitle);
      });
    }
    [Fact]
    public void ReplaceWarningShowsSizeAuthorAndChanges()
    {
      var text = SyncPanelViewModel.ReplaceSummary(new ReplacePreview { RemoteCount = 1240, LastPublishedBy = "Ana",
        LastPublishedAt = DateTimeOffset.UtcNow, Compared = true, Exact = true, Added = 12, Changed = 8, Removed = 37 });
      Assert.Contains("1.240 elementos, última revisión de Ana", text.Replace("1,240", "1.240"));
      Assert.Contains("+12 nuevos · 8 cambiados · −37 se quitarán", text);
      Assert.DoesNotContain("aproximadas", text);
      Assert.Contains("aproximadas", SyncPanelViewModel.ReplaceSummary(new ReplacePreview { Compared = true, Exact = false }));
      Assert.Contains("no se pudo comparar", SyncPanelViewModel.ReplaceSummary(null));
      Assert.Contains("Aún no hay una captura local", SyncPanelViewModel.ReplaceSummary(new ReplacePreview { RemoteCount = 3 }));
      Assert.Contains("de 1.240 elementos, reemplazada por Francis", SyncPanelViewModel.RestoreSummary(
        new OpenProject.Revit.LiveSync.Transport.RestorePointInfo { ElementCount = 1240, CreatedBy = "Francis" }).Replace("1,240", "1.240"));
    }
    [Fact]
    public void ReceiverStatusReadsTheModelGroupContract()
    {
      var status = Newtonsoft.Json.JsonConvert.DeserializeObject<OpenProject.Revit.LiveSync.Transport.ReceiverStatus>(
        "{\"accessLevel\":\"admin\",\"allowedActions\":[\"sync\",\"replace\"],\"modelExists\":true,\"userName\":\"Francis Vin\"}");
      var access = OpenProject.Revit.LiveSync.Transport.CdeModelAccess.From(status);
      Assert.Equal("Administración", access.GroupName);
      Assert.Equal("Francis Vin", access.UserName);
      Assert.True(access.CanReplace && access.CanSynchronize && !access.CanCreate && access.ModelExists == true);
      var legacy = OpenProject.Revit.LiveSync.Transport.CdeModelAccess.From(Newtonsoft.Json.JsonConvert.DeserializeObject<OpenProject.Revit.LiveSync.Transport.ReceiverStatus>("{}"));
      Assert.False(legacy.Known);
      Assert.True(legacy.CanSynchronize);
      Assert.False(legacy.CanReplace);
      var unknownLevel = OpenProject.Revit.LiveSync.Transport.CdeModelAccess.From(new OpenProject.Revit.LiveSync.Transport.ReceiverStatus { AccessLevel = "owner", AllowedActions = new[] { "replace" } });
      Assert.False(unknownLevel.CanReplace);
    }
    [Fact]
    public void HistoryControlLoadsAtNarrowDockWidth()
    {
      RunSta(() =>
      {
        using var folder = new OutboxTests.TestFolder();
        Directory.CreateDirectory(folder.Path);
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(folder.Path, "outbox.sqlite") + ";Pooling=False"))
        {
          db.Open();
          using var command = db.CreateCommand();
          command.CommandText = "CREATE TABLE history (id TEXT PRIMARY KEY,state TEXT,detail TEXT,time TEXT)";
          command.ExecuteNonQuery();
          for (var i = 0; i < 8; i++)
          {
            var state = i == 7 ? "Enviando" : i % 2 == 0 ? "Revisión aplicada" : "Guardado en destino";
            command.CommandText = $"INSERT INTO history VALUES ('revision-{i}','{state}','Evento {i}','{DateTime.UtcNow.AddMinutes(i):O}')";
            command.ExecuteNonQuery();
          }
        }
        var history = new SyncHistoryView();
        history.Update(folder.Path, new SyncProgress());
        PumpDispatcher(1200);
        history.Measure(new Size(280, 1000));
        history.Arrange(new Rect(0, 0, 280, 1000));
        history.UpdateLayout();
        Assert.True(history.DesiredSize.Width <= 280);
        Assert.Contains(Descendants(history).OfType<TextBlock>(), b => b.Text == "Cola e historial");
        Assert.Equal(5, Descendants(history).OfType<ToggleButton>().Count(b => (string)b.Tag == "HistoryEntry"));
        var iconChips = Descendants(history).OfType<Border>().Where(b => b.Width == 16 && b.Height == 16).ToArray();
        Assert.Equal(5, iconChips.Length);
        Assert.Equal(3, iconChips.Select(b => b.Background.ToString()).Distinct().Count());
        Assert.All(iconChips, b => Assert.Contains(Descendants(b).OfType<System.Windows.Shapes.Path>(), p =>
          p.Width == 10 && p.Height == 10 && p.Fill != null && p.Stroke == null));
        Assert.DoesNotContain(Descendants(history).OfType<TextBlock>(), b => b.Text.StartsWith("Mostrando "));
        var row = Descendants(history).OfType<ToggleButton>().First(b => (string)b.Tag == "HistoryEntry");
        row.IsChecked = true;
        history.UpdateLayout();
        var revision = Descendants(history).OfType<TextBox>().First(t => AutomationProperties.GetName(t) == "Identificador de revisión");
        Assert.True(revision.IsReadOnly);
        revision.Select(0, revision.Text.Length);
        Assert.Equal(revision.Text, revision.SelectedText);
        Assert.DoesNotContain(Descendants(history).OfType<Button>(), b => AutomationProperties.GetName(b) == "Copiar identificador de revisión");
        var preview = Environment.GetEnvironmentVariable("CMCOORDINATOR_HISTORY_PREVIEW");
        if (!string.IsNullOrEmpty(preview))
        {
          var bitmap = new RenderTargetBitmap(280, 600, 96, 96, PixelFormats.Pbgra32);
          bitmap.Render(history);
          var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
          using var output = File.Create(preview); png.Save(output);
          history.Measure(new Size(450, 600)); history.Arrange(new Rect(0, 0, 450, 600)); history.UpdateLayout();
          var wide = new RenderTargetBitmap(450, 600, 96, 96, PixelFormats.Pbgra32);
          wide.Render(history);
          var widePng = new PngBitmapEncoder(); widePng.Frames.Add(BitmapFrame.Create(wide));
          using var wideOutput = File.Create(Path.Combine(Path.GetDirectoryName(preview),
            Path.GetFileNameWithoutExtension(preview) + "-450.png")); widePng.Save(wideOutput);
        }
        history.Update(folder.Path, new SyncProgress { JobId = "revision-7" });
        Descendants(history).OfType<Button>().Single(b => (string)b.Tag == "En espera")
          .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpDispatcher(700); Layout(history);
        Assert.Single(Descendants(history).OfType<ToggleButton>().Where(b => (string)b.Tag == "HistoryEntry"));
        Assert.Contains(Descendants(history).OfType<TextBlock>(), b => b.Text == "En proceso");
        history.Update(folder.Path, new SyncProgress { ReceiverRevision = "revision-7", ViewerRevision = "revision-6" });
        PumpDispatcher(700); Layout(history);
        Assert.Empty(Descendants(history).OfType<ToggleButton>().Where(b => (string)b.Tag == "HistoryEntry"));
        Assert.True(Descendants(history).OfType<ScrollViewer>().First().ActualHeight >= 200);
        Assert.Equal(history.ActualWidth, Descendants(history).OfType<Button>().First(b => b.Name == "HeaderButton").ActualWidth, 1);
        if (!string.IsNullOrEmpty(preview))
        {
          var empty = new RenderTargetBitmap(360, 350, 96, 96, PixelFormats.Pbgra32);
          empty.Render(history);
          var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(empty));
          using var output = File.Create(Path.Combine(Path.GetDirectoryName(preview),
            Path.GetFileNameWithoutExtension(preview) + "-empty.png")); png.Save(output);
        }
        Descendants(history).OfType<Button>().Single(b => (string)b.Tag == "Procesados")
          .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpDispatcher(700); Layout(history);
        Assert.Equal(5, Descendants(history).OfType<ToggleButton>().Count(b => (string)b.Tag == "HistoryEntry"));
      });
    }

    private static void Layout(FrameworkElement panel)
    {
      panel.Measure(new Size(360, 1000)); panel.Arrange(new Rect(0, 0, 360, 1000)); panel.UpdateLayout();
      Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static void PumpDispatcher(int milliseconds)
    {
      var frame = new DispatcherFrame();
      var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
      timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
      timer.Start(); Dispatcher.PushFrame(frame);
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
      for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
      {
        var child = VisualTreeHelper.GetChild(root, i); yield return child;
        foreach (var descendant in Descendants(child)) yield return descendant;
      }
    }

    private static void RunSta(Action action)
    {
      Exception error = null;
      var thread = new Thread(() =>
      {
        try { action(); } catch (Exception ex) { error = ex; }
        finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
      }) { IsBackground = true };
      thread.SetApartmentState(ApartmentState.STA); thread.Start();
      Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF panel binding test timed out");
      if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
  }
}
