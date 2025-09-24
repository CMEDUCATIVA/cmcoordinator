using System.Windows;
using OpenProject.Browser.ViewModels;
using OpenProject.Browser.WebViewIntegration;

namespace OpenProject.Browser.Views
{
  /// <summary>
  /// Main panel UI and logic that need to be used by all modules
  /// </summary>
  public partial class WebView
  {
    public WebView(WebViewModel viewModel, JavaScriptBridge javaScriptBridge)
    {
      DataContext = viewModel;
      InitializeComponent();

      // After the bridge receives a message that triggers loading of a new url, we need to set the focus out of the
      // cefsharp browser. Otherwise a nasty bug occurs making the cefsharp embedded browser unable to set
      // the :focus meta tag.
      javaScriptBridge.OnNavigationEventReceived += sender =>
      {
        Application.Current.Dispatcher.Invoke(() => { Ghost.Focus(); });
      };
    }
  }
}
