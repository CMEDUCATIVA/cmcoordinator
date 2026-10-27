using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace OpenProject.Revit.LiveSync.Presentation
{
  public sealed record DrawingChoice(string ViewId, string Name, string ViewType);

  // Modal list of the document's 2D views, grouped by kind, with the same colours as the synchronization panel.
  public sealed class DrawingPickerWindow : Window
  {
    private static readonly (string Type, string Label)[] Groups =
    {
      ("FloorPlan", "Plantas"), ("CeilingPlan", "Plantas de techo"), ("EngineeringPlan", "Plantas estructurales"),
      ("AreaPlan", "Planos de área"), ("Section", "Secciones"), ("Elevation", "Alzados"),
      ("Detail", "Vistas de detalle"), ("DraftingView", "Vistas de dibujo"), ("Legend", "Leyendas")
    };
    private readonly List<(DrawingChoice Choice, CheckBox Box)> _boxes = new();
    private readonly TextBlock _count;
    private IReadOnlyList<string> _result;

    public static IReadOnlyList<string> Ask(IReadOnlyList<DrawingChoice> views, IEnumerable<string> selected, bool dark)
    {
      var window = new DrawingPickerWindow(views, new HashSet<string>(selected ?? Enumerable.Empty<string>(), StringComparer.Ordinal), dark);
      try { new WindowInteropHelper(window) { Owner = Process.GetCurrentProcess().MainWindowHandle }; }
      catch (InvalidOperationException) { /* Without an owner the window still opens centred on the screen. */ }
      window.ShowDialog();
      return window._result;
    }

    private DrawingPickerWindow(IReadOnlyList<DrawingChoice> views, HashSet<string> selected, bool dark)
    {
      var palette = dark ? new[] { "#101C27", "#142431", "#E3EDF1", "#A6BBC9", "#2B4050", "#78DDC4", "#0C2924" }
        : new[] { "#FFFFFF", "#F3F6F8", "#182B38", "#506475", "#D6E0E6", "#126853", "#FFFFFF" };
      Brush Brush(int index) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[index]));
      var background = Brush(0); var surface = Brush(1); var text = Brush(2); var muted = Brush(3); var border = Brush(4); var accent = Brush(5); var accentText = Brush(6);
      if (SystemParameters.HighContrast)
      {
        background = SystemColors.WindowBrush; surface = SystemColors.ControlBrush; text = muted = border = SystemColors.WindowTextBrush;
        accent = SystemColors.HighlightBrush; accentText = SystemColors.HighlightTextBrush;
      }
      Title = "Vistas 2D para sincronizar"; Width = 460; Height = 600; MinWidth = 360; MinHeight = 360;
      WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false;
      FontFamily = new FontFamily("Segoe UI"); FontSize = 14; Background = background; Foreground = text;

      var root = new DockPanel { Margin = new Thickness(18) };
      var intro = new TextBlock
      {
        Text = "Elige las plantas, secciones y alzados que se dibujan en vectorial y se mantienen al día mientras editas el modelo.",
        TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = muted, Margin = new Thickness(0, 0, 0, 10)
      };
      DockPanel.SetDock(intro, Dock.Top); root.Children.Add(intro);

      var filter = new TextBox { MinHeight = 34, Padding = new Thickness(7, 5, 7, 5), Background = surface, Foreground = text, BorderBrush = border, Margin = new Thickness(0, 0, 0, 10) };
      AutomationProperties.SetName(filter, "Buscar vista por nombre");
      DockPanel.SetDock(filter, Dock.Top); root.Children.Add(filter);

      var buttons = new Grid { Margin = new Thickness(0, 12, 0, 0) };
      buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
      buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
      _count = new TextBlock { FontSize = 12, Foreground = muted, VerticalAlignment = VerticalAlignment.Center };
      AutomationProperties.SetLiveSetting(_count, AutomationLiveSetting.Polite);
      buttons.Children.Add(_count);
      var actions = new StackPanel { Orientation = Orientation.Horizontal };
      Grid.SetColumn(actions, 2); buttons.Children.Add(actions);
      Button Make(string label, bool primary)
      {
        var button = new Button { Content = label, MinHeight = 36, MinWidth = 92, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(8, 0, 0, 0),
          Background = primary ? accent : surface, Foreground = primary ? accentText : text, BorderBrush = primary ? accent : border };
        AutomationProperties.SetName(button, label);
        actions.Children.Add(button);
        return button;
      }
      var none = Make("Ninguna", false);
      var cancel = Make("Cancelar", false); cancel.IsCancel = true;
      var save = Make("Guardar", true); save.IsDefault = true;
      DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);

      var list = new StackPanel();
      foreach (var (type, label) in Groups)
      {
        var items = views.Where(view => view.ViewType == type).ToList();
        if (items.Count == 0) continue;
        list.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = muted, Margin = new Thickness(0, 10, 0, 4) });
        foreach (var view in items)
        {
          var box = new CheckBox { Content = view.Name, IsChecked = selected.Contains(view.ViewId), Foreground = text, Margin = new Thickness(0, 3, 0, 3) };
          AutomationProperties.SetName(box, label + " · " + view.Name);
          box.Checked += (_, _) => Count(); box.Unchecked += (_, _) => Count();
          _boxes.Add((view, box)); list.Children.Add(box);
        }
      }
      if (_boxes.Count == 0)
        list.Children.Add(new TextBlock { Text = "Este modelo no tiene vistas 2D que se puedan dibujar.", Foreground = muted, TextWrapping = TextWrapping.Wrap });
      var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderBrush = border, BorderThickness = new Thickness(0, 1, 0, 1), Padding = new Thickness(0, 0, 6, 8) };
      root.Children.Add(scroll);

      filter.TextChanged += (_, _) =>
      {
        var query = filter.Text.Trim();
        foreach (var (choice, box) in _boxes)
          box.Visibility = query.Length == 0 || choice.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 ? Visibility.Visible : Visibility.Collapsed;
      };
      none.Click += (_, _) => { foreach (var (_, box) in _boxes) box.IsChecked = false; };
      save.Click += (_, _) => { _result = _boxes.Where(item => item.Box.IsChecked == true).Select(item => item.Choice.ViewId).ToList(); DialogResult = true; };
      Content = root;
      Count();
      Loaded += (_, _) => Keyboard.Focus(filter);
    }

    private void Count()
    {
      var chosen = _boxes.Count(item => item.Box.IsChecked == true);
      _count.Text = chosen == 1 ? "1 vista elegida" : $"{chosen} vistas elegidas";
    }
  }
}
