using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
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
        markup.Root.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Remove();
        foreach (var attribute in markup.Descendants().Attributes("Click").ToArray()) attribute.Remove();
        var panel = (UserControl)XamlReader.Parse(markup.ToString());
        var model = new SyncPanelViewModel();
        panel.DataContext = model;
        Layout(panel);
        var progress = Descendants(panel).OfType<ProgressBar>().Single();
        Assert.Equal(0d, progress.Value);
        Assert.Equal(BindingMode.OneWay, BindingOperations.GetBinding(progress, ProgressBar.ValueProperty).Mode);

        model.Refresh("Modelo de prueba", "Capturando", new SyncProgress
        {
          Stage = "Sincronizando", Done = 2, Total = 4, Paused = true,
          ReceiverRevision = "1234567890", ViewerRevision = "abcdefghij"
        }, "Diagnóstico local");
        Layout(panel);
        Assert.Equal(50d, progress.Value);
        Assert.Contains(Descendants(panel).OfType<Button>(), b => (string)b.Content == "▶ Continuar");
        Assert.Contains(Descendants(panel).OfType<TextBlock>(), b => b.Text == "12345678");
        Assert.Contains(Descendants(panel).OfType<TextBlock>(), b => b.Text == "abcdefgh");

        var expander = Descendants(panel).OfType<Expander>().Single();
        expander.IsExpanded = true;
        Layout(panel);
        Assert.Equal("Diagnóstico local", Descendants(panel).OfType<TextBox>().Single().Text);
        model.Refresh("Modelo de prueba", "Listo", new SyncProgress { Done = 4, Total = 4 }, "Finalizado");
        Layout(panel);
        Assert.Equal(100d, progress.Value);
        Assert.Equal("Finalizado", Descendants(panel).OfType<TextBox>().Single().Text);
        panel.DataContext = null;
      });
    }

    private static void Layout(FrameworkElement panel)
    {
      panel.Measure(new Size(360, 1000)); panel.Arrange(new Rect(0, 0, 360, 1000)); panel.UpdateLayout();
      Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
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
