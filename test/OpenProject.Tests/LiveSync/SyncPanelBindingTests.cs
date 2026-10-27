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
