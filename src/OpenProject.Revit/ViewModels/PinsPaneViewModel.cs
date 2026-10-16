using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using OpenProject.Revit.Models;
using OpenProject.Revit.Services;
using OpenProject.Shared.Pins;
using Serilog;
using System;
using OpenProject.Revit.Entry;
using System.Threading;
using System.Threading.Tasks;

namespace OpenProject.Revit.ViewModels
{
  public sealed class PinsPaneViewModel : INotifyPropertyChanged
  {
    private PinItemViewModel _selectedItem;
    private bool _isRefreshing;
    private bool _statusHighlightEnabled;
    private readonly List<PinItemViewModel> _allItems = new();
    private ProjectOption _selectedProject;
    private string _projectSearchText;
    private bool _isProjectLoading;
    private bool _hasExplicitProjectSelection;
    private bool _isUpdatingProjectsCollection;
    private ProjectOption _pendingProjectSelectionRestore;

    private static readonly ProjectOption AllProjectsOption = new()
    {
      IsAll = true,
      Name = "Todos los proyectos"
    };

    public ObservableCollection<PinItemViewModel> Items { get; } = new();
    public ObservableCollection<ProjectOption> Projects { get; } = new();

    public ICommand FocusSelectedCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand GoToUrlCommand { get; }
    public ICommand SearchProjectsCommand { get; }
    public ICommand LoadRecentProjectsCommand { get; }

    public PinItemViewModel SelectedItem
    {
      get => _selectedItem;
      set
      {
        if (_selectedItem == value) return;
        _selectedItem = value;
        OnPropertyChanged();
      }
    }

    public bool IsRefreshing
    {
      get => _isRefreshing;
      private set
      {
        if (_isRefreshing == value) return;
        _isRefreshing = value;
        OnPropertyChanged();
      }
    }

    public bool StatusHighlightEnabled
    {
      get => _statusHighlightEnabled;
      set
      {
        if (_statusHighlightEnabled == value) return;
        _statusHighlightEnabled = value;
        OnPropertyChanged();
        PinsPaneManager.SetStatusHighlightEnabled(value);
        var settings = McpSettingsStore.Load();
        settings.StatusHighlightEnabled = value;
        McpSettingsStore.Save(settings);
      }
    }

    public ProjectOption SelectedProject
    {
      get => _selectedProject;
      set
      {
        if (_isUpdatingProjectsCollection && value == null && _pendingProjectSelectionRestore != null)
        {
          Log.Information("SelectedProject setter: ignored transient null during project list refresh. pendingRestore={Pending}",
            _pendingProjectSelectionRestore.DisplayName);
          return;
        }

        if (ReferenceEquals(_selectedProject, value))
          return;

        Log.Information("SelectedProject setter: previous={Previous} incoming={Incoming}",
          _selectedProject?.DisplayName ?? "<null>",
          value?.DisplayName ?? "<null>");
        _selectedProject = value;
        _hasExplicitProjectSelection = value != null;
        Log.Information("SelectedProject setter: explicitSelection={HasExplicitSelection} effective={Effective}",
          _hasExplicitProjectSelection,
          _selectedProject?.DisplayName ?? "<null>");
        OnPropertyChanged();
        ApplyProjectFilter();
      }
    }

    public string ProjectSearchText
    {
      get => _projectSearchText;
      set
      {
        if (string.Equals(_projectSearchText, value, StringComparison.Ordinal))
          return;

        _projectSearchText = value;
        OnPropertyChanged();
      }
    }

    public bool IsProjectLoading
    {
      get => _isProjectLoading;
      private set
      {
        if (_isProjectLoading == value)
          return;

        _isProjectLoading = value;
        OnPropertyChanged();
      }
    }

    public PinsPaneViewModel()
    {
      var settings = McpSettingsStore.Load();
      _statusHighlightEnabled = settings.StatusHighlightEnabled;
      _selectedProject = null;

      FocusSelectedCommand = new DelegateCommand(() =>
      {
        var item = SelectedItem;
        if (item == null) return;

        if (!string.IsNullOrWhiteSpace(item.ViewpointJson))
        {
          Log.Information("FocusSelected: applying stored viewpoint for workPackageId={Id}", item.WorkPackageId ?? string.Empty);
          PinsPaneManager.OpenViewpointFromPin(item.Record);
          return;
        }

        if (item.HasSelection)
        {
          var id = item.WorkPackageId;
          if (string.IsNullOrWhiteSpace(id)) return;
          Log.Information("FocusSelected: focusing pin in best available view workPackageId={Id}", id);
          PinsManager.FocusPin(PinsPaneManager.UiApp, id, item.Record);
          return;
        }

        Log.Warning("FocusSelected: no selection and no viewpoint data.");
      });

      RefreshCommand = new DelegateCommand(() =>
      {
        RefreshAsync();
      });

      GoToUrlCommand = new DelegateCommand(() =>
      {
        OpenUrl(SelectedItem?.Url);
      });

      SearchProjectsCommand = new DelegateCommand(() =>
      {
        _ = SearchProjectsAsync();
      });

      LoadRecentProjectsCommand = new DelegateCommand(() =>
      {
        _ = LoadRecentProjectsAsync();
      });

      PinsPaneManager.SetStatusHighlightEnabled(_statusHighlightEnabled);
      _ = LoadRecentProjectsAsync();
    }

    private static void OpenUrl(string url)
    {
      if (string.IsNullOrWhiteSpace(url))
      {
        Log.Information("OpenUrl skipped (empty url).");
        return;
      }
      if (RibbonButtonClickHandler.IpcHandler == null)
      {
        Log.Warning("OpenUrl failed (IPC not ready).");
        return;
      }

      RibbonButtonClickHandler.IpcHandler.SendOpenUrlRequestToDesktopApp(url);
    }

    private async void RefreshAsync()
    {
      if (IsRefreshing)
        return;

      try
      {
        IsRefreshing = true;
        Log.Information("API refresh button clicked.");
        await PinsPaneManager.RefreshPinsFromApiAsync();
      }
      catch (Exception ex)
      {
        Log.Error(ex, "API refresh button failed.");
      }
      finally
      {
        IsRefreshing = false;
      }
    }

    public void LoadPins(IEnumerable<PinRecord> pins)
    {
      _allItems.Clear();
      if (pins == null)
      {
        Items.Clear();
        EnsureProjectsFromPinsFallback();
        return;
      }

      foreach (var pin in pins)
      {
        _allItems.Add(new PinItemViewModel(pin));
      }

      ApplyProjectFilter();
      EnsureProjectsFromPinsFallback();
      if (Projects.Count <= 3)
      {
        _ = LoadRecentProjectsAsync();
      }
    }

    private void ApplyProjectFilter()
    {
      Items.Clear();
      var project = SelectedProject;
      if (!_hasExplicitProjectSelection || project == null || project.IsAll)
      {
        Log.Information("Project filter inactive. explicitSelection={HasExplicitSelection} selected={Selected} isAll={IsAll} itemsShown=0",
          _hasExplicitProjectSelection,
          project?.DisplayName ?? "<null>",
          project?.IsAll ?? false);
        return;
      }

      foreach (var item in _allItems)
      {
        if (item.MatchesProject(project))
          Items.Add(item);
      }
      Log.Information("Project filter applied. explicitSelection={HasExplicitSelection} selected={Selected} itemsShown={Count}",
        _hasExplicitProjectSelection,
        project.DisplayName,
        Items.Count);
    }

    private async Task LoadRecentProjectsAsync()
    {
      if (IsProjectLoading)
      {
        Log.Information("LoadRecentProjectsAsync skipped because IsProjectLoading=true.");
        return;
      }

      IsProjectLoading = true;
      try
      {
        Log.Information("LoadRecentProjectsAsync started. currentSelected={Selected} searchText='{SearchText}'",
          SelectedProject?.DisplayName ?? "<null>",
          ProjectSearchText ?? string.Empty);
        var settings = McpSettingsStore.Load();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var recent = await McpClient.GetRecentProjectsAsync(settings, 10, cts.Token);
        UpdateProjectsCollection(recent, includePinsWhenEmpty: true, maxItems: 10);
        Log.Information("LoadRecentProjectsAsync finished. resultingItems={Count} selected={Selected}",
          Projects.Count,
          SelectedProject?.DisplayName ?? "<null>");
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to load recent projects.");
      }
      finally
      {
        IsProjectLoading = false;
      }
    }

    private async Task SearchProjectsAsync()
    {
      var text = (ProjectSearchText ?? string.Empty).Trim();
      Log.Information("SearchProjectsAsync started. rawSearchText='{SearchText}' trimmed='{Trimmed}' currentSelected={Selected}",
        ProjectSearchText ?? string.Empty,
        text,
        SelectedProject?.DisplayName ?? "<null>");
      if (string.Equals(text, AllProjectsOption.DisplayName, StringComparison.OrdinalIgnoreCase))
      {
        await LoadRecentProjectsAsync();
        return;
      }

      if (string.IsNullOrWhiteSpace(text))
      {
        await LoadRecentProjectsAsync();
        return;
      }

      if (IsProjectLoading)
        return;

      IsProjectLoading = true;
      try
      {
        var settings = McpSettingsStore.Load();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var projects = await McpClient.SearchProjectsAsync(settings, text, 100, cts.Token);
        UpdateProjectsCollection(projects, includePinsWhenEmpty: true, maxItems: 0);
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Project search failed.");
      }
      finally
      {
        IsProjectLoading = false;
      }
    }

    public void ResetProjectSearchAndReloadRecent()
    {
      var previousSearchText = ProjectSearchText ?? string.Empty;
      if (!string.IsNullOrEmpty(ProjectSearchText))
      {
        ProjectSearchText = string.Empty;
      }

      Log.Information("Project search reset after explicit selection. previousSearchText='{PreviousSearchText}' selectedProject={Selected}. Reloading recent projects list.",
        previousSearchText,
        SelectedProject?.DisplayName ?? "<null>");
      _ = LoadRecentProjectsAsync();
    }

    private void UpdateProjectsCollection(IEnumerable<ProjectOption> projects, bool includePinsWhenEmpty, int maxItems)
    {
      var current = SelectedProject;
      var incoming = (projects ?? Enumerable.Empty<ProjectOption>())
        .Where(p => p != null)
        .ToList();

      if (current != null && !current.IsAll)
      {
        var currentExistsInIncoming = incoming.Any(p =>
          p != null &&
          !p.IsAll &&
          (IsProjectKeyEqual(p.Identifier, current.Identifier) ||
           IsProjectKeyEqual(p.Id, current.Id)));

        if (!currentExistsInIncoming)
        {
          incoming.Insert(0, new ProjectOption
          {
            Id = current.Id,
            Identifier = current.Identifier,
            Name = current.Name,
            IsAll = false
          });
          Log.Information("Projects list refresh: injected current selected project into incoming list selected={Selected}",
            current.DisplayName);
        }
      }

      var fromPins = new List<ProjectOption>();
      if (includePinsWhenEmpty && incoming.Count == 0)
      {
        fromPins = GetProjectsFromPins();
        foreach (var pinProject in fromPins)
        {
          var exists = incoming.Any(p =>
            IsProjectKeyEqual(p.Identifier, pinProject.Identifier) ||
            IsProjectKeyEqual(p.Id, pinProject.Id));
          if (!exists)
            incoming.Add(pinProject);
        }
      }

      if (maxItems > 0 && incoming.Count > maxItems)
      {
        incoming = incoming.Take(maxItems).ToList();
      }

      var restoredSelection = ResolveRestoredProjectSelection(current, incoming);
      _isUpdatingProjectsCollection = true;
      _pendingProjectSelectionRestore = restoredSelection;
      try
      {
        Projects.Clear();
        Projects.Add(AllProjectsOption);
        foreach (var project in incoming)
          Projects.Add(project);
      }
      finally
      {
        _isUpdatingProjectsCollection = false;
        _pendingProjectSelectionRestore = null;
      }

      Log.Information("Projects list updated apiCount={ApiCount} pinsCount={PinsCount} totalCount={TotalCount} restoredSelection={RestoredSelection}",
        (projects ?? Enumerable.Empty<ProjectOption>()).Count(),
        fromPins.Count,
        Projects.Count,
        restoredSelection?.DisplayName ?? "<null>");

      if (restoredSelection != null)
      {
        SelectedProject = restoredSelection;
        return;
      }

      _selectedProject = null;
      _hasExplicitProjectSelection = false;
      OnPropertyChanged(nameof(SelectedProject));
      ApplyProjectFilter();
    }

    private ProjectOption ResolveRestoredProjectSelection(ProjectOption current, List<ProjectOption> incoming)
    {
      if (current == null)
        return null;

      if (current.IsAll)
        return AllProjectsOption;

      var match = incoming.FirstOrDefault(p =>
        p != null &&
        !p.IsAll &&
        (IsProjectKeyEqual(p.Identifier, current.Identifier) ||
         IsProjectKeyEqual(p.Id, current.Id)));

      if (match != null)
      {
        Log.Information("Projects list refresh: keeping selected project={Project}", match.DisplayName);
        return match;
      }

      Log.Information("Projects list refresh: previous selected project not found, preserving null selection instead of forcing Todos los proyectos. previous={Previous}",
        current.DisplayName);
      return null;
    }

    private void EnsureProjectsFromPinsFallback()
    {
      if (Projects.Count > 1)
        return;

      var fromPins = GetProjectsFromPins();
      if (fromPins.Count == 0)
        return;

      foreach (var project in fromPins)
      {
        if (Projects.Any(p => !p.IsAll &&
                              (IsProjectKeyEqual(p.Identifier, project.Identifier) ||
                               IsProjectKeyEqual(p.Id, project.Id))))
          continue;
        Projects.Add(project);
      }
      Log.Information("Projects fallback from pins applied count={Count}", fromPins.Count);
    }

    private List<ProjectOption> GetProjectsFromPins()
    {
      return _allItems
        .Where(i => !string.IsNullOrWhiteSpace(i.ProjectKey))
        .GroupBy(i => i.ProjectKey, StringComparer.OrdinalIgnoreCase)
        .Select(g =>
        {
          var first = g.First();
          return new ProjectOption
          {
            Id = !string.IsNullOrWhiteSpace(first.ProjectId) ? first.ProjectId : first.ProjectKey,
            Identifier = !string.IsNullOrWhiteSpace(first.ProjectIdentifier) ? first.ProjectIdentifier : first.ProjectKey,
            Name = !string.IsNullOrWhiteSpace(first.ProjectName) ? first.ProjectName : first.ProjectKey,
            IsAll = false
          };
        })
        .ToList();
    }

    private static bool IsProjectKeyEqual(string left, string right)
    {
      if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        return false;

      return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
  }
}
