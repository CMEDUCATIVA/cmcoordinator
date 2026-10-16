using System;
using System.Linq;
using System.Windows.Input;
using OpenProject.Revit.Models;
using OpenProject.Revit.Entry;
using OpenProject.Revit.Services;
using OpenProject.Shared.Pins;
using Serilog;

namespace OpenProject.Revit.ViewModels
{
  public sealed class PinItemViewModel
  {
    private readonly PinRecord _record;

    public PinItemViewModel(PinRecord record)
    {
      _record = record;
      FocusCommand = new DelegateCommand(() => PinsManager.FocusPin(PinsPaneManager.UiApp, WorkPackageId, _record));
      ViewCommand = new DelegateCommand(() =>
      {
        if (!string.IsNullOrWhiteSpace(ViewpointJson))
        {
          Log.Information("PinItemViewModel.ViewCommand using stored viewpoint. workPackageId={WorkPackageId}", WorkPackageId ?? string.Empty);
          PinsPaneManager.OpenViewpointFromPin(_record);
          return;
        }

        if (HasSelection)
        {
          if (string.IsNullOrWhiteSpace(WorkPackageId))
            return;
          Log.Information("PinItemViewModel.ViewCommand falling back to best selection view. workPackageId={WorkPackageId}", WorkPackageId);
          PinsManager.FocusPin(PinsPaneManager.UiApp, WorkPackageId, _record);
          return;
        }

        Log.Warning("PinItemViewModel.ViewCommand: no selection and no viewpoint data.");
      });
      GoCommand = new DelegateCommand(() =>
      {
        if (string.IsNullOrWhiteSpace(Url))
        {
          Log.Information("PinItemViewModel.GoCommand skipped (empty url).");
          return;
        }

        if (RibbonButtonClickHandler.IpcHandler == null)
        {
          Log.Warning("PinItemViewModel.GoCommand failed (IPC not ready).");
          return;
        }

        RibbonButtonClickHandler.IpcHandler.SendOpenUrlRequestToDesktopApp(Url);
      });
      ChatCommand = new DelegateCommand(() =>
      {
        if (string.IsNullOrWhiteSpace(WorkPackageId))
        {
          Log.Information("PinItemViewModel.ChatCommand skipped (empty workPackageId).");
          return;
        }

        PinChatWindowManager.OpenChat(_record);
      });
    }

    public string WorkPackageId => _record.WorkPackageId;
    public string Name => _record.Name;
    public string Type => _record.Type;
    public string Priority => _record.Priority;
    public string Status => _record.Status;
    public string StatusColor => _record.StatusColor;
    public string Assignee => _record.Assignee;
    public string Responsible => _record.Responsible;
    public string Url => _record.Url;
    public string SnapshotUrl => _record.SnapshotUrl;
    public string ProjectId => _record.ProjectId;
    public string ProjectIdentifier => _record.ProjectIdentifier;
    public string ProjectName => _record.ProjectName;
    public string ProjectKey => !string.IsNullOrWhiteSpace(ProjectIdentifier)
      ? ProjectIdentifier
      : !string.IsNullOrWhiteSpace(ProjectId)
        ? ProjectId
        : ExtractProjectKeyFromAnyUrl(Url, SnapshotUrl);
    public string ViewpointJson => _record.ViewpointJson;
    public bool HasSelection => _record.IfcGuids != null && _record.IfcGuids.Count > 0;

    public ICommand FocusCommand { get; }
    public ICommand ViewCommand { get; }
    public ICommand GoCommand { get; }
    public ICommand ChatCommand { get; }

    public PinRecord Record => _record;

    public bool MatchesProject(ProjectOption project)
    {
      if (project == null || project.IsAll)
        return true;

      var key = ProjectKey;
      if (string.IsNullOrWhiteSpace(key))
        return false;

      if (!string.IsNullOrWhiteSpace(ProjectIdentifier) &&
          !string.IsNullOrWhiteSpace(project.Identifier) &&
          string.Equals(ProjectIdentifier, project.Identifier, StringComparison.OrdinalIgnoreCase))
        return true;

      if (!string.IsNullOrWhiteSpace(ProjectId) &&
          !string.IsNullOrWhiteSpace(project.Id) &&
          string.Equals(ProjectId, project.Id, StringComparison.OrdinalIgnoreCase))
        return true;

      if (!string.IsNullOrWhiteSpace(project.Identifier) &&
          string.Equals(key, project.Identifier, StringComparison.OrdinalIgnoreCase))
        return true;

      if (!string.IsNullOrWhiteSpace(project.Id) &&
          string.Equals(key, project.Id, StringComparison.OrdinalIgnoreCase))
        return true;

      return false;
    }

    private static string ExtractProjectKeyFromAnyUrl(params string[] urls)
    {
      if (urls == null || urls.Length == 0)
        return null;

      foreach (var url in urls)
      {
        var key = ExtractProjectKeyFromUrl(url);
        if (!string.IsNullOrWhiteSpace(key))
          return key;
      }

      return null;
    }

    private static string ExtractProjectKeyFromUrl(string url)
    {
      if (string.IsNullOrWhiteSpace(url))
        return null;

      string path;
      if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
      {
        path = absolute.AbsolutePath;
      }
      else if (Uri.TryCreate(url, UriKind.Relative, out var relative))
      {
        path = relative.OriginalString;
      }
      else
      {
        return null;
      }

      var segments = path
        .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(Uri.UnescapeDataString)
        .ToArray();

      for (var i = 0; i < segments.Length - 1; i++)
      {
        if (!segments[i].Equals("projects", StringComparison.OrdinalIgnoreCase))
          continue;

        return segments[i + 1];
      }

      return null;
    }
  }
}
