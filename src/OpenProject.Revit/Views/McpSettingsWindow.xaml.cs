using System.Windows;

namespace OpenProject.Revit.Views
{
  public partial class McpSettingsWindow : Window
  {
    public McpSettingsWindow()
    {
      InitializeComponent();
      InitializeUpdates();
    }

    public void ShowUpdates(bool startDownload)
    {
      Show();
      Activate();
      if (startDownload) OnUpdateClick(UpdateButton, new RoutedEventArgs());
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
      Close();
    }
  }
}
