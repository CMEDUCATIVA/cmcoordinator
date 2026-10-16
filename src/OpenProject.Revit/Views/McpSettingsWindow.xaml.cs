using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using OpenProject.Revit.Models;
using OpenProject.Revit.Services;
using Serilog;

namespace OpenProject.Revit.Views
{
  public partial class McpSettingsWindow : Window
  {
    private readonly ObservableCollection<ColumnVisibilityItem> _columns = new();
    private McpSettings _loadedSettings;

    public McpSettingsWindow()
    {
      InitializeComponent();
      ColumnsItemsControl.ItemsSource = _columns;
      SectionList.SelectedIndex = 0;
      LoadSettings();
    }

    private void LoadSettings()
    {
      _loadedSettings = McpSettingsStore.Load();
      var browserUrl = ResolveBrowserBaseUrl();
      UrlTextBox.Text = browserUrl;
      ApiTokenBox.Password = _loadedSettings.ApiToken ?? string.Empty;
      AutoRefreshCheckBox.IsChecked = _loadedSettings.AutoRefreshEnabled;
      AutoRefreshValueTextBox.Text = _loadedSettings.AutoRefreshIntervalValue > 0
        ? _loadedSettings.AutoRefreshIntervalValue.ToString()
        : string.Empty;
      SelectAutoRefreshUnit(_loadedSettings.AutoRefreshIntervalUnit);
      DevToolsCheckBox.IsChecked = LoadDevToolsEnabledSetting(_loadedSettings);
      AiEnabledCheckBox.IsChecked = _loadedSettings.AiAgentEnabled;
      AiAutoConnectCheckBox.IsChecked = _loadedSettings.AiAutoConnect;
      AiBridgeUrlTextBox.Text = _loadedSettings.AiBridgeUrl ?? string.Empty;
      AiWorkstationIdTextBox.Text = _loadedSettings.AiWorkstationId ?? string.Empty;
      AiBridgeTokenBox.Password = _loadedSettings.AiBridgeToken ?? string.Empty;
      AiAgentIdTextBox.Text = _loadedSettings.AiAgentId ?? string.Empty;

      LoadColumns(_loadedSettings);
      UpdateConnectionVisualState(_loadedSettings.ApiConnected);
      Log.Information("Settings connection URL loaded from browser url={Url}", browserUrl);
    }

    private void LoadColumns(McpSettings settings)
    {
      _columns.Clear();
      var map = settings.ColumnVisibility ?? new Dictionary<string, bool>();

      AddColumn("ID", map);
      AddColumn("Nombre", map);
      AddColumn("URL", map);
      AddColumn("Tipo", map);
      AddColumn("Prioridad", map);
      AddColumn("Estado", map);
      AddColumn("Asignado", map);
      AddColumn("Responsable", map);
    }

    private void AddColumn(string key, Dictionary<string, bool> map)
    {
      var visible = true;
      if (map.TryGetValue(key, out var value))
        visible = value;

      _columns.Add(new ColumnVisibilityItem(key, key, visible));
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
      await SaveAndTestConnectionAsync(showMessage: true);
    }

    private async void OnConnectionStateClick(object sender, RoutedEventArgs e)
    {
      await SaveAndTestConnectionAsync(showMessage: true);
    }

    private async Task SaveAndTestConnectionAsync(bool showMessage)
    {
      var settings = BuildSettingsFromInputs();
      PersistSettings(settings);
      UpdateConnectionVisualState(false);

      var isConnected = false;
      try
      {
        ConnectButton.IsEnabled = false;
        ConnectionStateButton.IsEnabled = false;
        ConnectButton.Content = "Guardando...";
        ConnectionStateButton.Content = "Conectando...";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        isConnected = await McpClient.TestConnectionAsync(settings, cts.Token);
      }
      finally
      {
        ConnectButton.IsEnabled = true;
        ConnectionStateButton.IsEnabled = true;
        ConnectButton.Content = "Guardar";
      }

      settings.ApiConnected = isConnected;
      PersistSettings(settings);
      UpdateConnectionVisualState(isConnected);

      if (!showMessage)
        return;

      var title = "Conexion API";
      var message = isConnected
        ? "Conectado correctamente."
        : "No se pudo conectar. Verifica Api Token y URL del Browser.";
      MessageBox.Show(this, message, title, MessageBoxButton.OK,
        isConnected ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private McpSettings BuildSettingsFromInputs()
    {
      var autoRefreshValue = 0;
      if (!string.IsNullOrWhiteSpace(AutoRefreshValueTextBox.Text))
        int.TryParse(AutoRefreshValueTextBox.Text.Trim(), out autoRefreshValue);

      return new McpSettings
      {
        Url = ResolveBrowserBaseUrl(),
        ApiToken = ApiTokenBox.Password,
        ApiConnected = _loadedSettings?.ApiConnected == true,
        ColumnVisibility = _columns.ToDictionary(c => c.Key, c => c.IsChecked),
        ColumnOrder = _loadedSettings?.ColumnOrder,
        ColumnWidths = _loadedSettings?.ColumnWidths,
        AutoRefreshEnabled = AutoRefreshCheckBox.IsChecked == true,
        AutoRefreshIntervalValue = autoRefreshValue,
        AutoRefreshIntervalUnit = GetSelectedAutoRefreshUnit(),
        DevToolsEnabled = DevToolsCheckBox.IsChecked == true,
        AiAgentEnabled = AiEnabledCheckBox.IsChecked == true,
        AiAutoConnect = AiAutoConnectCheckBox.IsChecked == true,
        AiBridgeUrl = AiBridgeUrlTextBox.Text?.Trim(),
        AiWorkstationId = AiWorkstationIdTextBox.Text?.Trim(),
        AiBridgeToken = AiBridgeTokenBox.Password,
        AiAgentId = AiAgentIdTextBox.Text?.Trim()
      };
    }

    private void PersistSettings(McpSettings settings)
    {
      McpSettingsStore.Save(settings);
      SaveDevToolsEnabledSetting(settings.DevToolsEnabled);
      PinsPaneManager.ApplyColumnVisibility(settings);
      PinsPaneManager.ApplyAutoRefresh(settings);
      PinsPaneManager.SaveColumnState(settings);
      RevitBridgeClient.ApplySettings(settings);
      _loadedSettings = settings;
      Log.Information("API settings saved.");
    }

    private void UpdateConnectionVisualState(bool connected)
    {
      ConnectionStateButton.Content = connected ? "Conectado" : "Desconectado";
      ConnectionStateButton.Background = connected
        ? new SolidColorBrush(Color.FromRgb(46, 125, 50))
        : new SolidColorBrush(Color.FromRgb(117, 117, 117));
      ConnectionStateButton.Foreground = Brushes.White;
    }

    private static string ResolveBrowserBaseUrl()
    {
      var fallback = "https://erp.cmeducativa.es";
      var configPath = GetBrowserConfigurationPath();
      if (!File.Exists(configPath))
        return fallback;

      try
      {
        var json = File.ReadAllText(configPath);
        var jObject = JObject.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);

        var lastVisited = jObject["LastVisitedPage"]?.ToString();
        var fromLastVisited = NormalizeToBaseUrl(lastVisited);
        if (!string.IsNullOrWhiteSpace(fromLastVisited))
          return fromLastVisited;

        var instancesRaw = jObject["OpenProjectInstances"]?.ToString();
        if (!string.IsNullOrWhiteSpace(instancesRaw))
        {
          var array = JArray.Parse(instancesRaw);
          var first = array.FirstOrDefault()?.ToString();
          var fromInstances = NormalizeToBaseUrl(first);
          if (!string.IsNullOrWhiteSpace(fromInstances))
            return fromInstances;
        }
      }
      catch (Exception ex)
      {
        Log.Warning(ex, "Failed to resolve browser base URL from config.");
      }

      return fallback;
    }

    private static string NormalizeToBaseUrl(string url)
    {
      if (string.IsNullOrWhiteSpace(url))
        return null;

      if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        return null;

      var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port)
      {
        Path = string.Empty
      };

      return builder.Uri.ToString().TrimEnd('/');
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
      Close();
    }

    private void OnSectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
      var index = SectionList.SelectedIndex;
      ConnectionPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
      VisibilityPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
      AiAgentPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTestAiConnectionClick(object sender, RoutedEventArgs e)
    {
      var tempSettings = new McpSettings
      {
        AiAgentEnabled = AiEnabledCheckBox.IsChecked == true,
        AiAutoConnect = true,
        AiBridgeUrl = AiBridgeUrlTextBox.Text?.Trim(),
        AiWorkstationId = AiWorkstationIdTextBox.Text?.Trim(),
        AiBridgeToken = AiBridgeTokenBox.Password,
        AiAgentId = AiAgentIdTextBox.Text?.Trim()
      };

      RevitBridgeClient.ApplySettings(tempSettings);
      RevitBridgeClient.ReconnectNow();
      MessageBox.Show(this, "Intentando conectar el puente IA.", "IA", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SelectAutoRefreshUnit(string unit)
    {
      var unitValue = string.IsNullOrWhiteSpace(unit) ? "Minuto" : unit;
      foreach (var item in AutoRefreshUnitComboBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())
      {
        if (string.Equals(item.Content?.ToString(), unitValue, StringComparison.OrdinalIgnoreCase))
        {
          AutoRefreshUnitComboBox.SelectedItem = item;
          return;
        }
      }
    }

    private string GetSelectedAutoRefreshUnit()
    {
      if (AutoRefreshUnitComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem item)
        return item.Content?.ToString();
      return "Minuto";
    }

    private static bool LoadDevToolsEnabledSetting(McpSettings settings)
    {
      var configPath = GetBrowserConfigurationPath();
      if (!File.Exists(configPath))
        return settings?.DevToolsEnabled ?? false;

      try
      {
        var json = File.ReadAllText(configPath);
        var jObject = JObject.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var token = jObject["EnableDevelopmentTools"];
        if (token != null && bool.TryParse(token.ToString(), out var enabled))
        {
          if (settings != null)
            settings.DevToolsEnabled = enabled;
          return enabled;
        }
      }
      catch
      {
        // ignore invalid config
      }

      return settings?.DevToolsEnabled ?? false;
    }

    private static void SaveDevToolsEnabledSetting(bool enabled)
    {
      var configPath = GetBrowserConfigurationPath();
      try
      {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? string.Empty);
        JObject jObject;
        if (File.Exists(configPath))
        {
          var json = File.ReadAllText(configPath);
          jObject = JObject.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        }
        else
        {
          jObject = new JObject();
        }

        jObject["EnableDevelopmentTools"] = enabled;
        File.WriteAllText(configPath, jObject.ToString());
      }
      catch
      {
        // ignore config write issues
      }
    }

    private static string GetBrowserConfigurationPath()
    {
      var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
      return Path.Combine(appData, "OpenProject.Revit", "OpenProject.Configuration.json");
    }

    private sealed class ColumnVisibilityItem
    {
      public ColumnVisibilityItem(string key, string displayName, bool isChecked)
      {
        Key = key;
        DisplayName = displayName;
        IsChecked = isChecked;
      }

      public string Key { get; }
      public string DisplayName { get; }
      public bool IsChecked { get; set; }
    }
  }
}
