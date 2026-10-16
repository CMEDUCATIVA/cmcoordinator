using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenProject.Revit.Models;
using OpenProject.Revit.Services;
using OpenProject.Revit.ViewModels;

namespace OpenProject.Revit.Views
{
  public partial class PinsPaneView : UserControl
  {
    private bool _suppressColumnSave;
    private RotateTransform _refreshRotate;
    private DispatcherTimer _refreshTimer;
    private double _refreshAngle;
    private DispatcherTimer _autoRefreshTimer;
    private DispatcherTimer _widthSaveTimer;
    private Dictionary<string, double> _lastSavedWidths;
    private ProjectOption _lastValidProjectSelection;
    private bool _projectSelectionChangedWhileDropDownOpen;

    public PinsPaneView()
    {
      InitializeComponent();
      var vm = new PinsPaneViewModel();
      DataContext = vm;
      PinsPaneManager.Initialize(vm);
      PinsPaneManager.RegisterView(this);

      _refreshRotate = RefreshIconRotate;
      _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
      _refreshTimer.Tick += (_, _) =>
      {
        _refreshAngle = (_refreshAngle + 12) % 360;
        if (_refreshRotate != null)
          _refreshRotate.Angle = _refreshAngle;
      };

      _autoRefreshTimer = new DispatcherTimer();
      _autoRefreshTimer.Tick += (_, _) =>
      {
        try
        {
          if (vm.IsRefreshing)
            return;

          if (vm.RefreshCommand?.CanExecute(null) == true)
            vm.RefreshCommand.Execute(null);
        }
        catch (Exception ex)
        {
          Serilog.Log.Error(ex, "Auto refresh tick failed.");
          _autoRefreshTimer.Stop();
        }
      };

      _widthSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
      _widthSaveTimer.Tick += (_, _) =>
      {
        _widthSaveTimer.Stop();
        PersistColumnWidthsIfChanged();
      };

      var settings = McpSettingsStore.Load();
      ApplyColumnVisibility(settings);
      ApplyAutoRefresh(settings);

      vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    public void ApplyColumnVisibility(McpSettings settings)
    {
      ApplyColumnOrder(settings);
      ApplyColumnWidths(settings);
      var map = settings?.ColumnVisibility;
      SetColumnVisibility(IdColumn, map, "ID");
      SetColumnVisibility(NombreColumn, map, "Nombre");
      SetColumnVisibility(UrlColumn, map, "URL");
      SetColumnVisibility(TipoColumn, map, "Tipo");
      SetColumnVisibility(PrioridadColumn, map, "Prioridad");
      SetColumnVisibility(EstadoColumn, map, "Estado");
      SetColumnVisibility(AsignadoColumn, map, "Asignado");
      SetColumnVisibility(ResponsableColumn, map, "Responsable");
    }

    public void ApplyAutoRefresh(McpSettings settings)
    {
      if (_autoRefreshTimer == null)
        return;

      var enabled = settings?.AutoRefreshEnabled == true;
      var interval = BuildAutoRefreshInterval(settings);

      if (!enabled || interval == null)
      {
        _autoRefreshTimer.Stop();
        return;
      }

      _autoRefreshTimer.Interval = interval.Value;
      _autoRefreshTimer.Start();
      Serilog.Log.Information("Auto refresh enabled interval={Interval}", interval.Value);
    }

    private static TimeSpan? BuildAutoRefreshInterval(McpSettings settings)
    {
      if (settings == null)
        return null;

      if (settings.AutoRefreshIntervalValue <= 0)
        return null;

      var unit = settings.AutoRefreshIntervalUnit ?? "Minuto";
      return unit switch
      {
        "Segundo" => TimeSpan.FromSeconds(settings.AutoRefreshIntervalValue),
        "Minuto" => TimeSpan.FromMinutes(settings.AutoRefreshIntervalValue),
        "Hora" => TimeSpan.FromHours(settings.AutoRefreshIntervalValue),
        _ => TimeSpan.FromMinutes(settings.AutoRefreshIntervalValue)
      };
    }

    private void ApplyColumnOrder(McpSettings settings)
    {
      var order = settings?.ColumnOrder;
      if (order == null || order.Count == 0)
        return;

      var map = GetColumnMap();
      if (!order.Contains("Acciones"))
        order.Insert(0, "Acciones");
      else if (order[0] != "Acciones")
      {
        order.Remove("Acciones");
        order.Insert(0, "Acciones");
      }

      _suppressColumnSave = true;
      try
      {
        var index = 0;
        foreach (var key in order)
        {
          if (map.TryGetValue(key, out var column))
            column.DisplayIndex = index++;
        }

        foreach (var pair in map.Where(p => !order.Contains(p.Key)))
        {
          pair.Value.DisplayIndex = index++;
        }
      }
      finally
      {
        _suppressColumnSave = false;
      }
    }

    private void ApplyColumnWidths(McpSettings settings)
    {
      var widths = settings?.ColumnWidths;
      if (widths == null || widths.Count == 0)
        return;

      var map = GetColumnMap();
      _suppressColumnSave = true;
      try
      {
        foreach (var pair in widths)
        {
          if (!map.TryGetValue(pair.Key, out var column))
            continue;

          if (pair.Value <= 0)
            continue;

          column.Width = new DataGridLength(pair.Value);
        }
      }
      finally
      {
        _suppressColumnSave = false;
      }
    }

    private static void SetColumnVisibility(DataGridColumn column, Dictionary<string, bool> map, string key)
    {
      if (column == null)
        return;

      var visible = true;
      if (map != null && map.TryGetValue(key, out var value))
        visible = value;

      column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private Dictionary<string, DataGridColumn> GetColumnMap()
    {
      return new Dictionary<string, DataGridColumn>
      {
        ["Acciones"] = AccionesColumn,
        ["ID"] = IdColumn,
        ["Nombre"] = NombreColumn,
        ["URL"] = UrlColumn,
        ["Tipo"] = TipoColumn,
        ["Prioridad"] = PrioridadColumn,
        ["Estado"] = EstadoColumn,
        ["Asignado"] = AsignadoColumn,
        ["Responsable"] = ResponsableColumn
      };
    }

    private void OnColumnReordered(object sender, DataGridColumnEventArgs e)
    {
      if (_suppressColumnSave)
        return;

      var settings = McpSettingsStore.Load();
      var map = GetColumnMap();
      var ordered = map.OrderBy(p => p.Value.DisplayIndex).Select(p => p.Key).ToList();
      ordered.Remove("Acciones");
      ordered.Insert(0, "Acciones");
      settings.ColumnOrder = ordered;
      McpSettingsStore.Save(settings);
      PinsPaneManager.SaveColumnState(settings);
      PinsPaneManager.ApplyAutoRefresh(settings);
    }

    private void OnGridLayoutUpdated(object sender, EventArgs e)
    {
      try
      {
        if (_suppressColumnSave)
          return;

        // Debounce layout changes (column resize triggers multiple updates).
        _widthSaveTimer.Stop();
        _widthSaveTimer.Start();
      }
      catch (Exception ex)
      {
        Serilog.Log.Error(ex, "Grid layout update failed.");
      }
    }

    private void OnProjectDropDownOpened(object sender, EventArgs e)
    {
      try
      {
        _projectSelectionChangedWhileDropDownOpen = false;
        Serilog.Log.Information("ProjectDropDownOpened: items={Count} selected={Selected}",
          ProjectComboBox?.Items.Count ?? 0,
          DescribeProjectOption(ProjectComboBox?.SelectedItem as ProjectOption));

        if (DataContext is PinsPaneViewModel vm && vm.SelectedProject != null)
          _lastValidProjectSelection = vm.SelectedProject;

        Dispatcher.BeginInvoke(new Action(() =>
        {
          var searchTextBox = GetProjectSearchTextBox();
          if (searchTextBox == null)
          {
            Serilog.Log.Warning("ProjectDropDownOpened: search textbox not found in template.");
            return;
          }

          searchTextBox.Focus();
          searchTextBox.SelectAll();
          Serilog.Log.Information("ProjectDropDownOpened: focused ProjectSearchTextBox.");
        }), DispatcherPriority.Input);
      }
      catch (Exception ex)
      {
        Serilog.Log.Error(ex, "Failed to focus project dropdown search box.");
      }
    }

    private void OnProjectDropDownClosed(object sender, EventArgs e)
    {
      try
      {
        if (DataContext is not PinsPaneViewModel vm)
          return;

        Serilog.Log.Information(
          "ProjectDropDownClosed: changedWhileOpen={Changed} selected={Selected}",
          _projectSelectionChangedWhileDropDownOpen,
          DescribeProjectOption(vm.SelectedProject));

        if (!_projectSelectionChangedWhileDropDownOpen)
        {
          vm.ProjectSearchText = string.Empty;
          Serilog.Log.Information("ProjectDropDownClosed: closed without explicit selection. search text cleared, selection preserved as {Selected}",
            DescribeProjectOption(vm.SelectedProject));
        }
        else
        {
          Serilog.Log.Information("ProjectDropDownClosed: explicit selection kept. changedWhileOpen={Changed} selected={Selected}",
            _projectSelectionChangedWhileDropDownOpen,
            DescribeProjectOption(vm.SelectedProject));
        }
      }
      catch (Exception ex)
      {
        Serilog.Log.Error(ex, "Failed handling ProjectDropDownClosed.");
      }
    }

    private void OnProjectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      try
      {
        var added = string.Join(", ", e.AddedItems.OfType<ProjectOption>().Select(DescribeProjectOption));
        var removed = string.Join(", ", e.RemovedItems.OfType<ProjectOption>().Select(DescribeProjectOption));
        Serilog.Log.Information("ProjectSelectionChanged: added=[{Added}] removed=[{Removed}] current={Current}",
          added, removed, DescribeProjectOption(ProjectComboBox?.SelectedItem as ProjectOption));

        if (DataContext is not PinsPaneViewModel vm)
          return;

        if (ProjectComboBox?.SelectedItem is not ProjectOption selected)
          return;

        _projectSelectionChangedWhileDropDownOpen = true;
        _lastValidProjectSelection = selected;
        Serilog.Log.Information("ProjectSelectionChanged: handling explicit selection whileDropDownOpen={Changed} selected={Selected}",
          _projectSelectionChangedWhileDropDownOpen,
          DescribeProjectOption(selected));
        vm.ResetProjectSearchAndReloadRecent();
        Serilog.Log.Information("ProjectSelectionChanged: reset project search and requested recent projects reload after selection.");
        Serilog.Log.Information("ProjectSelectionChanged: accepted project={Project}", DescribeProjectOption(selected));
      }
      catch (Exception ex)
      {
        Serilog.Log.Error(ex, "Failed to normalize project selection.");
      }
    }

    private void OnProjectComboBoxPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
      try
      {
        if (ProjectComboBox == null || ProjectComboBox.IsDropDownOpen)
          return;

        if (FindVisualParent<ComboBoxItem>(e.OriginalSource as DependencyObject) != null)
          return;

        if (FindVisualParent<ToggleButton>(e.OriginalSource as DependencyObject) != null)
          return;

        ProjectComboBox.IsDropDownOpen = true;
        ProjectComboBox.Focus();
        e.Handled = true;
        Serilog.Log.Information("ProjectComboBoxMouseDown: opened dropdown from box click.");
      }
      catch (Exception ex)
      {
        Serilog.Log.Error(ex, "Failed handling project combo box click.");
      }
    }

    private void OnSearchTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
      try
      {
        // Prevent ComboBox from navigating/selecting list rows while typing in search box.
        if (e.Key == Key.Up ||
            e.Key == Key.Down ||
            e.Key == Key.PageUp ||
            e.Key == Key.PageDown ||
            e.Key == Key.Home ||
            e.Key == Key.End)
        {
          e.Handled = true;
          Serilog.Log.Information("SearchTextBoxKeyDown: swallowed key={Key} to avoid row selection.", e.Key);
        }
      }
      catch (Exception ex)
      {
        Serilog.Log.Error(ex, "Failed handling SearchTextBox key input.");
      }
    }

    private static string DescribeProjectOption(ProjectOption option)
    {
      if (option == null)
        return "<null>";

      return $"Display='{option.DisplayName}', Id='{option.Id}', Identifier='{option.Identifier}', IsAll={option.IsAll}, IsSearchBox={option.IsSearchBox}";
    }

    private TextBox GetProjectSearchTextBox()
    {
      return ProjectComboBox?.Template?.FindName("PART_ProjectSearchTextBox", ProjectComboBox) as TextBox;
    }

    private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
      while (child != null)
      {
        if (child is T match)
          return match;

        child = VisualTreeHelper.GetParent(child);
      }

      return null;
    }

    private void PersistColumnWidthsIfChanged()
    {
      try
      {
        if (_suppressColumnSave)
          return;

        var map = GetColumnMap();
        var widths = new Dictionary<string, double>();
        foreach (var pair in map)
        {
          var width = pair.Value?.ActualWidth ?? 0;
          if (width <= 0)
            continue;

          widths[pair.Key] = width;
        }

        if (_lastSavedWidths != null && WidthsEqual(_lastSavedWidths, widths))
          return;

        var settings = McpSettingsStore.Load();
        settings.ColumnWidths = widths;
        McpSettingsStore.Save(settings);
        PinsPaneManager.SaveColumnState(settings);
        PinsPaneManager.ApplyAutoRefresh(settings);
        _lastSavedWidths = widths;
      }
      catch (Exception ex)
      {
        Serilog.Log.Error(ex, "Failed to persist column widths.");
      }
    }

    private static bool WidthsEqual(Dictionary<string, double> left, Dictionary<string, double> right)
    {
      if (left.Count != right.Count)
        return false;

      foreach (var pair in left)
      {
        if (!right.TryGetValue(pair.Key, out var value))
          return false;

        if (Math.Abs(pair.Value - value) > 0.5)
          return false;
      }

      return true;
    }

    private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
      if (e.PropertyName != nameof(PinsPaneViewModel.IsRefreshing))
        return;

      if (DataContext is PinsPaneViewModel vm)
      {
        if (vm.IsRefreshing)
          _refreshTimer.Start();
        else
          _refreshTimer.Stop();
      }
    }
  }
}
