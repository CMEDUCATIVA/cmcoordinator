using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using OpenProject.Revit.Models;
using OpenProject.Revit.Services;
using Serilog;

namespace OpenProject.Revit.ViewModels
{
  public sealed class PinChatViewModel : INotifyPropertyChanged
  {
    private readonly string _workPackageId;
    private readonly string _pinName;
    private readonly McpSettings _settings;
    private string _currentMessage;
    private bool _isSending;

    public PinChatViewModel(string workPackageId, string pinName, McpSettings settings, PinChatHistory history)
    {
      _workPackageId = workPackageId ?? string.Empty;
      _pinName = pinName ?? string.Empty;
      _settings = settings;
      Messages = new ObservableCollection<PinChatMessage>();

      if (history?.Messages != null && history.Messages.TryGetValue(_workPackageId, out var list))
      {
        foreach (var msg in list.OrderBy(m => m.Timestamp))
          Messages.Add(msg);
      }

      SendCommand = new DelegateCommand(async () => await SendAsync());
    }

    public ObservableCollection<PinChatMessage> Messages { get; }

    public string CurrentMessage
    {
      get => _currentMessage;
      set
      {
        if (_currentMessage == value)
          return;
        _currentMessage = value;
        OnPropertyChanged(nameof(CurrentMessage));
      }
    }

    public ICommand SendCommand { get; }

    public string WorkPackageId => _workPackageId;
    public string PinName => _pinName;

    public bool IsSending
    {
      get => _isSending;
      set
      {
        if (_isSending == value)
          return;
        _isSending = value;
        OnPropertyChanged(nameof(IsSending));
      }
    }

    private async Task SendAsync()
    {
      if (IsSending)
        return;

      var text = (CurrentMessage ?? string.Empty).Trim();
      if (string.IsNullOrWhiteSpace(text))
        return;

      IsSending = true;
      try
      {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ok = await McpClient.AddWorkPackageCommentAsync(_workPackageId, text, _settings, cts.Token);
        if (!ok)
        {
          Log.Warning("PinChat send failed workPackageId={Id}", _workPackageId);
          return;
        }

        var message = new PinChatMessage
        {
          Text = text,
          Timestamp = DateTimeOffset.Now
        };

        Messages.Add(message);
        CurrentMessage = string.Empty;
        PinChatService.AppendMessage(_workPackageId, message);
      }
      catch (Exception ex)
      {
        Log.Error(ex, "PinChat send failed workPackageId={Id}", _workPackageId);
      }
      finally
      {
        IsSending = false;
      }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
  }
}
