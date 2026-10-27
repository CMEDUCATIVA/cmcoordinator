using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenProject.Revit.LiveSync.Queue;

namespace OpenProject.Revit.LiveSync.Presentation
{
  public partial class SyncHistoryView : UserControl
  {
    private readonly ObservableCollection<EntryView> _items = new();
    private string _directory, _filter = "Todos", _fingerprint;
    private string _activeJobId;
    private int _visible = 5, _generation;
    private int _total;
    private bool _ready, _busy, _collapsed;
    private DateTime _lastRead;
    public SyncHistoryView()
    {
      InitializeComponent();
      Events.ItemsSource = _items;
      _ready = true;
      PaintFilter();
    }

    public void Update(string directory, SyncProgress progress)
    {
      if (!_ready) return;
      var pending = progress?.HasPendingChanges == true;
      var processing = progress?.JobId != null;
      QueueState.Text = progress?.Attention == true ? "Atención" : processing
        ? "En proceso" : pending ? "En espera" : "Al día";
      QueueDot.Fill = progress?.Attention == true ? Brushes.OrangeRed : processing || pending
        ? Brushes.Goldenrod : Brushes.LimeGreen;
      var fingerprint = directory + "/" + progress?.JobId + "/" +
        string.Join(";", progress?.Jobs?.Select(j => j.Id + j.State + j.Time) ?? Array.Empty<string>());
      if (fingerprint == _fingerprint && DateTime.UtcNow - _lastRead < TimeSpan.FromSeconds(3)) return;
      _fingerprint = fingerprint;
      _directory = directory;
      _activeJobId = progress?.JobId;
      Reload();
    }

    private async Task LoadAsync()
    {
      if (_busy) return;
      _busy = true;
      var generation = ++_generation;
      var directory = _directory;
      var filter = _filter;
      var limit = _visible;
      var activeJobId = _activeJobId;
      try
      {
        var result = await Task.Run(() => SyncHistoryReader.Read(directory, filter, 0, limit,
          activeJobId: activeJobId)).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
        if (generation != _generation) return;
        _total = result.Total;
        CountBadge.Text = result.Available.ToString();
        EmptyText.Text = "Sin registros para este filtro";
        var position = ListScroll.VerticalOffset;
        for (var i = 0; i < result.Items.Count; i++)
        {
          var source = result.Items[i];
          var existing = _items.FirstOrDefault(item => item.Id == source.Id);
          if (existing == null) { existing = new EntryView(source); _items.Insert(i, existing); }
          else
          {
            existing.Update(source);
            var old = _items.IndexOf(existing);
            if (old != i) _items.Move(old, i);
          }
        }
        while (_items.Count > result.Items.Count) _items.RemoveAt(_items.Count - 1);
        ListScroll.ScrollToVerticalOffset(position);
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.Content = $"Mostrar más ({Math.Min(5, Math.Max(0, _total - _items.Count))})";
        MoreButton.Visibility = _items.Count < _total ? Visibility.Visible : Visibility.Collapsed;
        });
      }
      catch (Exception)
      {
        await Dispatcher.InvokeAsync(() =>
        {
          EmptyText.Text = "No se pudo leer el historial local; se reintentará.";
          EmptyText.Visibility = Visibility.Visible;
        });
      }
      finally
      {
        await Dispatcher.InvokeAsync(() =>
        {
          _lastRead = DateTime.UtcNow; _busy = false;
          if (generation != _generation) _ = LoadAsync();
        });
      }
    }

    private void Reload()
    {
      _lastRead = DateTime.MinValue;
      _generation++;
      _ = LoadAsync();
    }
    private void ToggleClick(object sender, RoutedEventArgs e)
    {
      _collapsed = !_collapsed;
      ContentArea.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
      Chevron.Data = Geometry.Parse(_collapsed ? "M8,4 L16,12 L8,20" : "M4,8 L12,16 L20,8");
    }
    private void FilterClick(object sender, RoutedEventArgs e)
    {
      _filter = (sender as Button)?.Tag as string ?? "Todos";
      _visible = 5;
      PaintFilter(); Reload();
    }
    private void PaintFilter()
    {
      foreach (var button in new[] { AllButton, WaitingButton, ProcessedButton })
      {
        var selected = (string)button.Tag == _filter;
        button.Foreground = selected ? new SolidColorBrush(Color.FromRgb(18, 104, 83)) : new SolidColorBrush(Color.FromRgb(80, 100, 117));
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        button.BorderBrush = selected ? button.Foreground : new SolidColorBrush(Color.FromRgb(214, 224, 230));
        button.BorderThickness = selected ? new Thickness(0, 0, 0, 2) : new Thickness(0, 0, 0, 1);
      }
    }
    private void MoreClick(object sender, RoutedEventArgs e) { _visible += 5; Reload(); }
    private sealed class EntryView : INotifyPropertyChanged
    {
      public event PropertyChangedEventHandler PropertyChanged;
      private bool _isExpanded;
      public bool IsExpanded
      {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value;
          PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); }
      }
      public string Id { get; }
      public string State { get; private set; }
      public string Detail { get; private set; }
      public string ShortDetail => Detail.Length > 90 ? Detail.Substring(0, 89) + "…" : Detail;
      public Geometry IconGeometry { get; private set; }
      public Brush IconForeground { get; private set; }
      public Brush IconBackground { get; private set; }
      public string Clock { get; private set; }
      public string DateAndTime { get; private set; }
      public EntryView(SyncHistoryReader.Entry entry) { Id = entry.Id; Update(entry); }
      public void Update(SyncHistoryReader.Entry entry)
      {
        if (State == entry.State && Detail == entry.Detail && Clock == entry.Clock) return;
        State = entry.State; Detail = entry.Detail; IconGeometry = entry.State switch
        {
          "Revisión aplicada" => HistoryFilledIcons.Applied,
          "Reconciliado" => HistoryFilledIcons.Reconciled,
          "Requiere atención" => HistoryFilledIcons.Attention,
          "Guardado en destino" when entry.IsWaiting => HistoryFilledIcons.Waiting,
          "Guardado en destino" => HistoryFilledIcons.Saved,
          "Enviando" => HistoryFilledIcons.Sending,
          "Validando revisión" => HistoryFilledIcons.Validating,
          "En cola" => HistoryFilledIcons.Waiting,
          _ => HistoryFilledIcons.Unknown
        };
        var (ink, fill) = entry.State switch
        {
          "Revisión aplicada" => (Color.FromRgb(13, 107, 61), Color.FromRgb(225, 246, 233)),
          "Reconciliado" => (Color.FromRgb(148, 98, 0), Color.FromRgb(255, 242, 217)),
          "Requiere atención" => (Color.FromRgb(180, 35, 24), Color.FromRgb(253, 233, 231)),
          "Guardado en destino" when entry.IsWaiting => (Color.FromRgb(138, 86, 0), Color.FromRgb(255, 241, 214)),
          "Guardado en destino" => (Color.FromRgb(20, 100, 155), Color.FromRgb(228, 242, 255)),
          "Enviando" => (Color.FromRgb(99, 54, 161), Color.FromRgb(240, 233, 252)),
          "Validando revisión" => (Color.FromRgb(11, 114, 133), Color.FromRgb(222, 245, 248)),
          _ => (Color.FromRgb(91, 102, 112), Color.FromRgb(238, 241, 244))
        };
        IconForeground = new SolidColorBrush(ink); IconBackground = new SolidColorBrush(fill);
        Clock = entry.Clock; DateAndTime = entry.DateAndTime;
        foreach (var name in new[] { nameof(State), nameof(Detail), nameof(ShortDetail), nameof(IconGeometry), nameof(IconForeground), nameof(IconBackground), nameof(Clock), nameof(DateAndTime) })
          PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
      }
    }
  }
}
