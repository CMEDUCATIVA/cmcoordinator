using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenProject.Revit.Models;
using Serilog;

namespace OpenProject.Revit.Services
{
  public static class McpClient
  {
    private static readonly HttpClient _http = new HttpClient();

    public sealed class WorkPackageInfo
    {
      public string ProjectId { get; set; }
      public string ProjectIdentifier { get; set; }
      public string ProjectName { get; set; }
      public string Name { get; set; }
      public string Type { get; set; }
      public string Priority { get; set; }
      public string Status { get; set; }
      public string StatusColor { get; set; }
      public string Assignee { get; set; }
      public string Responsible { get; set; }
    }

    public static async Task<List<ProjectOption>> GetRecentProjectsAsync(McpSettings settings, int pageSize, CancellationToken ct)
    {
      var fromSara = await GetProjectsFromSaraEndpointAsync(settings, null, pageSize, ct).ConfigureAwait(false);
      if (fromSara.Count > 0)
      {
        Log.Information("Projects loaded from Sara endpoint count={Count}", fromSara.Count);
        return fromSara;
      }

      var attempts = new List<(string Name, object Filters)>
      {
        ("active", new[] { CreateFilter("active", "=", "t") }),
        ("no-filters", null)
      };

      var projects = await GetProjectsWithFallbackAsync(settings, pageSize, null, attempts, ct).ConfigureAwait(false);
      if (projects.Count > 0)
        return projects.Take(Math.Max(1, pageSize)).ToList();

      // Fallback for instances where /projects is not available or filtered out.
      var fromWorkPackages = await GetRecentProjectsFromWorkPackagesAsync(settings, Math.Max(50, pageSize * 20), ct).ConfigureAwait(false);
      return fromWorkPackages.Take(Math.Max(1, pageSize)).ToList();
    }

    public static async Task<List<ProjectOption>> SearchProjectsAsync(
      McpSettings settings,
      string searchText,
      int pageSize,
      CancellationToken ct)
    {
      var text = (searchText ?? string.Empty).Trim();
      var fromSara = await GetProjectsFromSaraEndpointAsync(settings, text, pageSize, ct).ConfigureAwait(false);
      if (fromSara.Count > 0)
      {
        Log.Information("Projects search from Sara endpoint search='{Search}' count={Count}", text, fromSara.Count);
        return fromSara;
      }

      var filters = new List<Dictionary<string, object>>
      {
        CreateFilter("active", "=", "t")
      };

      if (!string.IsNullOrWhiteSpace(text))
      {
        filters.Add(CreateFilter("typeahead", "**", text));
      }

      var attempts = new List<(string Name, object Filters)>
      {
        ("active+typeahead", filters.ToArray())
      };

      if (!string.IsNullOrWhiteSpace(text))
      {
        attempts.Add(("typeahead-only", new[] { CreateFilter("typeahead", "**", text) }));
        attempts.Add(("nameAndIdentifier", new[] { CreateFilter("nameAndIdentifier", "**", text) }));
        attempts.Add(("name_and_identifier", new[] { CreateFilter("name_and_identifier", "**", text) }));
      }
      else
      {
        attempts.Add(("active-only", new[] { CreateFilter("active", "=", "t") }));
      }

      attempts.Add(("no-filters", null));

      var projects = await GetProjectsWithFallbackAsync(settings, pageSize, text, attempts, ct).ConfigureAwait(false);
      if (projects.Count > 0 || string.IsNullOrWhiteSpace(text))
        return projects;

      var localFiltered = projects
        .Where(p => ProjectMatchesText(p, text))
        .ToList();
      if (localFiltered.Count > 0)
        return localFiltered;

      var broad = await GetProjectsWithFallbackAsync(settings, 200, null, new List<(string, object)> { ("no-filters", null) }, ct).ConfigureAwait(false);
      return broad.Where(p => ProjectMatchesText(p, text)).ToList();
    }

    private static async Task<List<ProjectOption>> GetProjectsFromSaraEndpointAsync(
      McpSettings settings,
      string searchText,
      int maxCount,
      CancellationToken ct)
    {
      if (settings == null || string.IsNullOrWhiteSpace(settings.Url) || string.IsNullOrWhiteSpace(settings.ApiToken))
        return new List<ProjectOption>();

      try
      {
        var userId = await GetCurrentUserIdAsync(settings, ct).ConfigureAwait(false);
        var query = new StringBuilder("active_only=true&scope=todos");
        if (!string.IsNullOrWhiteSpace(searchText))
          query.Append("&search=").Append(Uri.EscapeDataString(searchText.Trim()));
        if (!string.IsNullOrWhiteSpace(userId))
          query.Append("&user_id=").Append(Uri.EscapeDataString(userId));

        var uri = BuildSaraUri(settings.Url, $"/ia_colaborativa/search_projects?{query}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyApiTokenAuth(request, settings.ApiToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var sw = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        sw.Stop();

        Log.Information("API GET Sara projects status={Status} ms={Ms} url={Url}",
          (int)response.StatusCode, sw.ElapsedMilliseconds, uri);

        if (!response.IsSuccessStatusCode)
        {
          var snippet = body ?? string.Empty;
          if (snippet.Length > 500)
            snippet = snippet.Substring(0, 500);
          Log.Warning("Sara projects endpoint non-success status={Status} body='{Body}'",
            (int)response.StatusCode,
            snippet);
          return new List<ProjectOption>();
        }

        var parsed = ParseSaraProjects(body);
        if (parsed.Count == 0)
          return parsed;

        var limit = Math.Max(1, maxCount);
        return parsed.Take(limit).ToList();
      }
      catch (OperationCanceledException)
      {
        return new List<ProjectOption>();
      }
      catch (Exception ex)
      {
        Log.Warning(ex, "Sara projects endpoint request failed.");
        return new List<ProjectOption>();
      }
    }

    private static async Task<List<ProjectOption>> GetProjectsWithFallbackAsync(
      McpSettings settings,
      int pageSize,
      string searchText,
      List<(string Name, object Filters)> attempts,
      CancellationToken ct)
    {
      if (attempts == null || attempts.Count == 0)
        return new List<ProjectOption>();

      List<ProjectOption> latestBody = null;
      foreach (var attempt in attempts)
      {
        var response = await RequestProjectsAsync(settings, pageSize, searchText, attempt.Name, attempt.Filters, ct).ConfigureAwait(false);
        if (response.Success)
          return response.Projects;

        latestBody = response.Projects;
        if (response.StatusCode != 400 && response.StatusCode != 422)
          break;

        Log.Warning("API projects fallback activated. attempt={Attempt} status={Status}", attempt.Name, response.StatusCode);
      }

      return latestBody ?? new List<ProjectOption>();
    }

    private static async Task<(bool Success, int StatusCode, List<ProjectOption> Projects)> RequestProjectsAsync(
      McpSettings settings,
      int pageSize,
      string searchText,
      string attemptName,
      object filters,
      CancellationToken ct)
    {
      if (settings == null || string.IsNullOrWhiteSpace(settings.Url) || string.IsNullOrWhiteSpace(settings.ApiToken))
        return (false, 0, new List<ProjectOption>());

      try
      {
        var pageSizeValue = Math.Max(1, pageSize);
        var path = $"/projects?pageSize={pageSizeValue}";
        if (filters != null)
        {
          var filtersJson = JsonConvert.SerializeObject(filters);
          path += $"&filters={Uri.EscapeDataString(filtersJson)}";
        }
        var uri = BuildApiUri(settings.Url, path);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyApiTokenAuth(request, settings.ApiToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/hal+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var sw = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        sw.Stop();

        Log.Information("API GET projects attempt={Attempt} search='{Search}' status={Status} ms={Ms} url={Url}",
          attemptName ?? string.Empty,
          searchText ?? string.Empty,
          (int)response.StatusCode,
          sw.ElapsedMilliseconds,
          uri);

        if (!response.IsSuccessStatusCode)
        {
          var snippet = body ?? string.Empty;
          if (snippet.Length > 500)
            snippet = snippet.Substring(0, 500);
          Log.Warning("API projects non-success attempt={Attempt} status={Status} body='{Body}'",
            attemptName ?? string.Empty,
            (int)response.StatusCode,
            snippet);
          return (false, (int)response.StatusCode, new List<ProjectOption>());
        }

        return (true, (int)response.StatusCode, ParseProjects(body));
      }
      catch (OperationCanceledException)
      {
        return (false, 0, new List<ProjectOption>());
      }
      catch (Exception ex)
      {
        Log.Error(ex, "API projects request failed. attempt={Attempt} search='{Search}'", attemptName ?? string.Empty, searchText ?? string.Empty);
        return (false, 0, new List<ProjectOption>());
      }
    }

    public static async Task<bool> TestConnectionAsync(McpSettings settings, CancellationToken ct)
    {
      if (settings == null || string.IsNullOrWhiteSpace(settings.Url) || string.IsNullOrWhiteSpace(settings.ApiToken))
        return false;

      try
      {
        var uri = BuildApiUri(settings.Url, string.Empty);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyApiTokenAuth(request, settings.ApiToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/hal+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var sw = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        sw.Stop();
        Log.Information("API test connection status={Status} ms={Ms} url={Url}",
          (int)response.StatusCode, sw.ElapsedMilliseconds, uri);
        return response.IsSuccessStatusCode;
      }
      catch (OperationCanceledException)
      {
        return false;
      }
      catch (Exception ex)
      {
        Log.Error(ex, "API test connection failed.");
        return false;
      }
    }

    public static async Task<WorkPackageInfo> GetWorkPackageInfoAsync(string workPackageId, McpSettings settings, CancellationToken ct)
    {
      if (settings == null)
        return null;
      if (string.IsNullOrWhiteSpace(settings.Url))
      {
        Log.Warning("API URL missing. Skipping work package lookup.");
        return null;
      }
      if (string.IsNullOrWhiteSpace(settings.ApiToken))
      {
        Log.Warning("API token missing. Skipping work package lookup.");
        return null;
      }
      if (string.IsNullOrWhiteSpace(workPackageId))
        return null;

      try
      {
        var uri = BuildApiUri(settings.Url, $"/work_packages/{Uri.EscapeDataString(workPackageId)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyApiTokenAuth(request, settings.ApiToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/hal+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var sw = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        sw.Stop();

        Log.Information("API GET work package id={Id} status={Status} ms={Ms} url={Url}",
          workPackageId, (int)response.StatusCode, sw.ElapsedMilliseconds, uri);

        if (!response.IsSuccessStatusCode)
          return null;

        var json = JObject.Parse(body);
        return ExtractInfo(json);
      }
      catch (OperationCanceledException)
      {
        return null;
      }
      catch (Exception ex)
      {
        Log.Error(ex, "API request failed for workPackageId={Id}", workPackageId);
        return null;
      }
    }

    public static async Task<bool> AddWorkPackageCommentAsync(string workPackageId, string comment, McpSettings settings, CancellationToken ct)
    {
      if (settings == null)
        return false;
      if (string.IsNullOrWhiteSpace(settings.Url))
      {
        Log.Warning("API URL missing. Skipping add comment.");
        return false;
      }
      if (string.IsNullOrWhiteSpace(settings.ApiToken))
      {
        Log.Warning("API token missing. Skipping add comment.");
        return false;
      }
      if (string.IsNullOrWhiteSpace(workPackageId) || string.IsNullOrWhiteSpace(comment))
        return false;

      try
      {
        var uri = BuildApiUri(settings.Url, $"/work_packages/{Uri.EscapeDataString(workPackageId)}/activities");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        ApplyApiTokenAuth(request, settings.ApiToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/hal+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(
          JsonConvert.SerializeObject(new
          {
            comment = new
            {
              raw = comment
            }
          }),
          Encoding.UTF8,
          "application/json");

        var sw = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        sw.Stop();

        Log.Information("API POST comment id={Id} status={Status} ms={Ms} url={Url}",
          workPackageId, (int)response.StatusCode, sw.ElapsedMilliseconds, uri);

        return response.IsSuccessStatusCode;
      }
      catch (OperationCanceledException)
      {
        return false;
      }
      catch (Exception ex)
      {
        Log.Error(ex, "API add comment failed for workPackageId={Id}", workPackageId);
        return false;
      }
    }

    private static void ApplyApiTokenAuth(HttpRequestMessage request, string apiToken)
    {
      var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"apikey:{apiToken}"));
      request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    private static Uri BuildApiUri(string baseUrl, string relativePath)
    {
      var normalizedBase = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
      if (normalizedBase.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase))
      {
        var finalPath = relativePath ?? string.Empty;
        if (finalPath.StartsWith("/", StringComparison.Ordinal))
          finalPath = finalPath.Substring(1);
        return new Uri($"{normalizedBase}/{finalPath}".TrimEnd('/'));
      }

      var idx = normalizedBase.IndexOf("/api/v3", StringComparison.OrdinalIgnoreCase);
      if (idx >= 0)
      {
        normalizedBase = normalizedBase.Substring(0, idx + "/api/v3".Length);
      }
      else
      {
        normalizedBase = $"{normalizedBase}/api/v3";
      }

      var path = relativePath ?? string.Empty;
      if (path.StartsWith("/", StringComparison.Ordinal))
        path = path.Substring(1);

      return new Uri(string.IsNullOrWhiteSpace(path)
        ? normalizedBase
        : $"{normalizedBase}/{path}");
    }

    private static Dictionary<string, object> CreateFilter(string name, string op, params string[] values)
    {
      return new Dictionary<string, object>
      {
        [name] = new Dictionary<string, object>
        {
          ["operator"] = op,
          ["values"] = values ?? Array.Empty<string>()
        }
      };
    }

    private static bool ProjectMatchesText(ProjectOption project, string text)
    {
      if (project == null)
        return false;
      if (string.IsNullOrWhiteSpace(text))
        return true;

      var needle = text.Trim();
      return ContainsIgnoreCase(project.Name, needle) ||
             ContainsIgnoreCase(project.Identifier, needle) ||
             ContainsIgnoreCase(project.Id, needle);
    }

    private static bool ContainsIgnoreCase(string source, string value)
    {
      if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(value))
        return false;

      return source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static async Task<List<ProjectOption>> GetRecentProjectsFromWorkPackagesAsync(
      McpSettings settings,
      int pageSize,
      CancellationToken ct)
    {
      if (settings == null || string.IsNullOrWhiteSpace(settings.Url) || string.IsNullOrWhiteSpace(settings.ApiToken))
        return new List<ProjectOption>();

      try
      {
        var pageSizeValue = Math.Max(1, pageSize);
        var path = $"/work_packages?pageSize={pageSizeValue}";
        var uri = BuildApiUri(settings.Url, path);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyApiTokenAuth(request, settings.ApiToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/hal+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var sw = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        sw.Stop();

        Log.Information("API GET work_packages for projects status={Status} ms={Ms} url={Url}",
          (int)response.StatusCode, sw.ElapsedMilliseconds, uri);

        if (!response.IsSuccessStatusCode)
        {
          var snippet = body ?? string.Empty;
          if (snippet.Length > 500)
            snippet = snippet.Substring(0, 500);
          Log.Warning("API work_packages project fallback failed status={Status} body='{Body}'",
            (int)response.StatusCode,
            snippet);
          return new List<ProjectOption>();
        }

        return ParseProjectsFromWorkPackages(body);
      }
      catch (OperationCanceledException)
      {
        return new List<ProjectOption>();
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to load projects from work_packages fallback.");
        return new List<ProjectOption>();
      }
    }

    private static List<ProjectOption> ParseProjectsFromWorkPackages(string json)
    {
      var root = JObject.Parse(json);
      var elements = root["_embedded"]?["elements"] as JArray;
      if (elements == null || elements.Count == 0)
        return new List<ProjectOption>();

      var items = new List<ProjectOption>();
      foreach (var token in elements.OfType<JObject>())
      {
        var projectLink = token["_links"]?["project"] as JObject;
        if (projectLink == null)
          continue;

        var href = projectLink["href"]?.ToString();
        var title = projectLink["title"]?.ToString();
        var key = ExtractProjectKeyFromApiSelfHref(href);
        if (string.IsNullOrWhiteSpace(key))
          continue;

        items.Add(new ProjectOption
        {
          Id = key,
          Identifier = key,
          Name = title,
          IsAll = false
        });
      }

      return items
        .GroupBy(p => BuildProjectGroupKey(p), StringComparer.OrdinalIgnoreCase)
        .Select(g => g.First())
        .ToList();
    }

    private static List<ProjectOption> ParseProjects(string json)
    {
      var root = JObject.Parse(json);
      var elements = root["_embedded"]?["elements"] as JArray;
      if (elements == null || elements.Count == 0)
        return new List<ProjectOption>();

      var items = new List<ProjectOption>();
      foreach (var token in elements.OfType<JObject>())
      {
        var id = token["id"]?.ToString();
        var identifier = token["identifier"]?.ToString();
        var name = token["name"]?.ToString() ?? token["subject"]?.ToString();

        if (string.IsNullOrWhiteSpace(identifier))
        {
          var selfHref = token["_links"]?["self"]?["href"]?.ToString();
          identifier = ExtractProjectKeyFromApiSelfHref(selfHref);
        }

        if (string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(identifier))
          id = identifier;

        var option = new ProjectOption
        {
          Id = id,
          Identifier = identifier,
          Name = name,
          IsAll = false
        };

        if (string.IsNullOrWhiteSpace(option.Id) &&
            string.IsNullOrWhiteSpace(option.Identifier) &&
            string.IsNullOrWhiteSpace(option.Name))
          continue;

        items.Add(option);
      }

      return items
        .GroupBy(p => BuildProjectGroupKey(p), StringComparer.OrdinalIgnoreCase)
        .Select(g => g.First())
        .ToList();
    }

    private static string BuildProjectGroupKey(ProjectOption option)
    {
      if (!string.IsNullOrWhiteSpace(option?.Identifier))
        return $"idf:{option.Identifier}";
      if (!string.IsNullOrWhiteSpace(option?.Id))
        return $"id:{option.Id}";
      return option?.Name ?? string.Empty;
    }

    private static string ExtractProjectKeyFromApiSelfHref(string selfHref)
    {
      if (string.IsNullOrWhiteSpace(selfHref))
        return null;

      var candidate = selfHref;
      if (Uri.TryCreate(selfHref, UriKind.Absolute, out var absoluteUri))
        candidate = absoluteUri.AbsolutePath;

      var segments = candidate
        .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(s => Uri.UnescapeDataString(s))
        .ToArray();

      for (var i = 0; i < segments.Length - 1; i++)
      {
        if (!segments[i].Equals("projects", StringComparison.OrdinalIgnoreCase))
          continue;

        return segments[i + 1];
      }

      return null;
    }

    private static Uri BuildSaraUri(string baseUrl, string relativePathWithQuery)
    {
      var normalizedBase = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
      var idx = normalizedBase.IndexOf("/api/v3", StringComparison.OrdinalIgnoreCase);
      if (idx >= 0)
        normalizedBase = normalizedBase.Substring(0, idx);

      var path = relativePathWithQuery ?? string.Empty;
      if (!path.StartsWith("/", StringComparison.Ordinal))
        path = "/" + path;

      return new Uri($"{normalizedBase}{path}");
    }

    private static async Task<string> GetCurrentUserIdAsync(McpSettings settings, CancellationToken ct)
    {
      try
      {
        var uri = BuildApiUri(settings.Url, "/users/me");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyApiTokenAuth(request, settings.ApiToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/hal+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
          return null;

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var root = JObject.Parse(body);
        return root["id"]?.ToString();
      }
      catch (Exception ex)
      {
        Log.Warning(ex, "Failed to resolve current user id for Sara projects endpoint.");
        return null;
      }
    }

    private static List<ProjectOption> ParseSaraProjects(string json)
    {
      if (string.IsNullOrWhiteSpace(json))
        return new List<ProjectOption>();

      var root = JObject.Parse(json);
      var success = root["success"]?.Value<bool>() ?? false;
      if (!success)
        return new List<ProjectOption>();

      var array = root["projects"] as JArray;
      if (array == null || array.Count == 0)
        return new List<ProjectOption>();

      var items = array
        .OfType<JObject>()
        .Select(raw => new
        {
          Project = new ProjectOption
          {
            Id = raw["id"]?.ToString(),
            Identifier = raw["identifier"]?.ToString(),
            Name = raw["name"]?.ToString(),
            IsAll = false
          },
          UpdatedAt = ParseDate(raw["updated_at"]?.ToString())
        })
        .Where(x => x.Project != null &&
                    (!string.IsNullOrWhiteSpace(x.Project.Id) ||
                     !string.IsNullOrWhiteSpace(x.Project.Identifier) ||
                     !string.IsNullOrWhiteSpace(x.Project.Name)))
        .GroupBy(x => BuildProjectGroupKey(x.Project), StringComparer.OrdinalIgnoreCase)
        .Select(g => g
          .OrderByDescending(i => i.UpdatedAt)
          .First()
          .Project)
        .OrderByDescending(p => ParseDate(array
          .OfType<JObject>()
          .FirstOrDefault(x =>
            string.Equals(x["id"]?.ToString(), p.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x["identifier"]?.ToString(), p.Identifier, StringComparison.OrdinalIgnoreCase))
          ?["updated_at"]?.ToString()))
        .ThenBy(p => p.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
        .ToList();

      return items;
    }

    private static DateTime ParseDate(string value)
    {
      if (DateTime.TryParse(value, out var parsed))
        return parsed;
      return DateTime.MinValue;
    }

    private static WorkPackageInfo ExtractInfo(JObject json)
    {
      var projectLink = json["_links"]?["project"] as JObject;
      var projectHref = projectLink?["href"]?.ToString();
      var projectTitle = projectLink?["title"]?.ToString();
      var projectIdentifier = ExtractProjectKeyFromApiSelfHref(projectHref);

      return new WorkPackageInfo
      {
        ProjectId = projectIdentifier,
        ProjectIdentifier = projectIdentifier,
        ProjectName = projectTitle,
        Name = json["subject"]?.ToString()
               ?? json["name"]?.ToString()
               ?? json["title"]?.ToString(),
        Type = EmbeddedName(json, "type"),
        Priority = EmbeddedName(json, "priority"),
        Status = EmbeddedName(json, "status"),
        StatusColor = json["_embedded"]?["status"]?["color"]?.ToString(),
        Assignee = EmbeddedName(json, "assignee"),
        Responsible = EmbeddedName(json, "responsible")
      };
    }

    private static string EmbeddedName(JObject json, string key)
    {
      return json["_embedded"]?[key]?["name"]?.ToString()
             ?? json["_embedded"]?[key]?["_links"]?["self"]?["title"]?.ToString()
             ?? json["_links"]?[key]?["title"]?.ToString();
    }
  }
}
