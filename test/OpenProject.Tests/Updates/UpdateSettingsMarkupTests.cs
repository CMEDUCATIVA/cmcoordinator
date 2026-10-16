using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using Xunit;

namespace OpenProject.Tests.Updates
{
  public class UpdateSettingsMarkupTests
  {
    [Fact]
    public void SettingsMarkupLoadsWithUpdateControlsWithoutRevitOrNetwork()
    {
      Exception failure = null;
      var thread = new Thread(() =>
      {
        try
        {
          using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Settings.McpSettingsWindow.xaml");
          using var reader = new StreamReader(resource);
          var document = XDocument.Parse(reader.ReadToEnd());
          document.Root.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Remove();
          foreach (var attribute in document.Descendants().Attributes().Where(a => a.Name == "Click" || a.Name == "SelectionChanged").ToList()) attribute.Remove();
          var window = (Window)XamlReader.Parse(document.ToString());
          Assert.Equal(700, window.Width); Assert.Equal(460, window.Height);
          var sections = (ListBox)window.FindName("SectionList");
          Assert.Equal("Actualizaciones", ((ListBoxItem)sections.Items[3]).Content);
          Assert.Equal("Actualizar desde GitHub", ((Button)window.FindName("UpdateButton")).Content);
          Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("CancelUpdateButton")).Visibility);
          Assert.NotNull(window.FindName("UpdateStatusText"));
          window.Close();
        }
        catch (Exception error) { failure = error; }
      });
      thread.SetApartmentState(ApartmentState.STA); thread.Start();
      Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
      if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
  }
}
