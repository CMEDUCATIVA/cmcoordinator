using System;
using System.Windows;
using OpenProject.Browser.Views;

namespace OpenProject.Browser.ViewModels
{
  /// <summary>
  /// The view model for the main window control.
  /// </summary>
  public sealed class MainWindowViewModel
  {
    private const double _windowMinWidth = 320.00;

    /// <summary>
    /// The view of the nested bcfier panel.
    /// </summary>
    public WebView WebView { get; }

    /// <summary>
    /// The main window calculated width.
    /// </summary>
    public double MinWidth => _windowMinWidth;

    /// <summary>
    /// The main window calculated width.
    /// </summary>
    public double Width => Math.Max(_windowMinWidth, SystemParameters.PrimaryScreenWidth * 0.33);

    /// <summary>
    /// The main window default height.
    /// </summary>
    public double Height => SystemParameters.WorkArea.Height;

    /// <summary>
    /// The main window default top margin.
    /// </summary>
    public double Top => 0;

    /// <summary>
    /// The main window default left margin.
    /// </summary>
    public double Left => SystemParameters.WorkArea.Width - Width;

    /// <summary>
    /// Constructor of the view model of the main window control.
    /// </summary>
    /// <param name="webView">The view of the nested bcfier panel.</param>
    public MainWindowViewModel(WebView webView)
    {
      WebView = webView;
    }
  }
}
