using System.Windows;
using OpenProject.Browser.ViewModels;
using OpenProject.Browser.WebViewIntegration;

namespace OpenProject.Browser.Views
{
  /// <summary>
  /// The view class for the main window.
  /// </summary>
  public partial class MainWindowView
  {
    /// <summary>
    /// Constructor for the main window control. Data context viewmodel is injected into constructor parameters.
    /// </summary>
    /// <param name="viewModel">The view model of the main window control.</param>
    /// <param name="javaScriptBridge">The java script bridge.</param>
    public MainWindowView(MainWindowViewModel viewModel, JavaScriptBridge javaScriptBridge)
    {
      DataContext = viewModel;

      // We have to set layout here, as the layout in wpf doesn't work with bindings at runtime.
      Width = viewModel.Width;
      MinWidth = viewModel.MinWidth;
      Height = viewModel.Height;
      Top = viewModel.Top;
      Left = viewModel.Left;

      InitializeComponent();

      javaScriptBridge.OnAppForegroundRequestReceived += sender =>
      {
        Application.Current.Dispatcher.Invoke(Activate);
      };
    }
  }
}
