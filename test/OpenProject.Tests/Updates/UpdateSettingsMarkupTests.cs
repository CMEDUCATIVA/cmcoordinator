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
          foreach (var attribute in document.Descendants().Attributes().Where(a => a.Name == "Click").ToList()) attribute.Remove();
          var window = (Window)XamlReader.Parse(document.ToString());
          // Only «Actualizaciones» remains: no section list, no Conexión, Visibilidad or Agente IA.
          Assert.Equal("Actualizaciones", window.Title);
          Assert.Null(window.FindName("SectionList"));
          Assert.Null(window.FindName("ApiTokenBox"));
          Assert.Null(window.FindName("AiAgentPanel"));
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
