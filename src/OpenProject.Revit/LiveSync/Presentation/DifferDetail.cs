using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenProject.Revit.LiveSync.Queue;

namespace OpenProject.Revit.LiveSync.Presentation
{
  /// <summary>What the panel and «Cola e historial» say about an element whose change did not enter the CDE.</summary>
  public static class DifferDetail
  {
    public static string Title(int count) => count == 1 ? "1 elemento no entró" : $"{count:N0} elementos no entraron";

    /// <summary>The state of the queue: «Al día · 3 fuera» says that being up to date leaves those changes out.</summary>
    public static string QueueState(SyncProgress progress)
    {
      if (progress?.Attention == true) return "Atención";
      if (progress?.JobId != null) return "En proceso";
      if (progress?.HasPendingChanges == true) return "En espera";
      var outside = progress?.Differs?.Count ?? 0;
      return outside > 0 ? $"Al día · {outside:N0} fuera" : "Al día";
    }

    public static string Name(DifferingElement item) => string.IsNullOrWhiteSpace(item.Name) ? item.Id : item.Name;

    // v1, v2 when the CDE numbers its elements; without numbers, what stayed out.
    public static string Versions(DifferingElement item)
    {
      if (item.Mine != null && item.Theirs != null) return $"Tu copia v{item.Mine} · CDE v{item.Theirs}";
      if (item.Theirs != null) return $"Tu copia es anterior · CDE v{item.Theirs}";
      if (item.Mine != null) return $"Tu copia v{item.Mine} · el CDE tiene una versión posterior";
      var aspect = Aspect(item);
      return aspect == null ? "Tu copia es anterior a la del CDE" : "No entró: " + aspect;
    }

    public static string Who(DifferingElement item) => string.IsNullOrWhiteSpace(item.By) ? null : "Quién: " + item.By;

    public static string When(DifferingElement item) =>
      DateTimeOffset.TryParse(item.At, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
        ? "Cuándo: " + at.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) : null;

    public static string Reason(DifferingElement item)
    {
      var who = string.IsNullOrWhiteSpace(item.By) ? "Otra persona" : item.By;
      var text = item.Reason switch
      {
        "host" => "El elemento que lo aloja cambió después de tu versión; este lo siguió.",
        "hostRejected" => "El elemento que lo aloja tampoco entró, así que este queda fuera con él.",
        "removed" => $"{who} borró este elemento después de tu versión.",
        "notRestored" => "Está en tu Revit, pero no en el modelo restaurado. No se envía solo: fuérzalo si debe volver al modelo.",
        "joined" => "Iba unido a otro elemento que no entró; queda fuera con él para que no se rompa la unión.",
        "restored" => $"{who} restauró el modelo después de tu versión; tu elemento es distinto del restaurado.",
        _ when IsRemoval(item) => $"{who} cambió este elemento después de tu versión, así que tu borrado no entró.",
        _ when Aspect(item) is { } aspect => $"{who} cambió {aspect} después de tu versión; en el CDE se conserva lo suyo.",
        _ => $"{who} cambió este elemento después de tu versión; en el CDE se conserva lo suyo."
      };
      return "Motivo: " + text + (item.Partial ? " El resto de tu cambio sí entró." : "");
    }

    /// <summary>The lines shown when the element is unfolded; what is not known is left out.</summary>
    public static IReadOnlyList<string> Lines(DifferingElement item) =>
      new[] { Versions(item), Who(item), When(item), Reason(item) }.Where(line => line != null).ToList();

    private static bool IsRemoval(DifferingElement item) => item.Aspects?.Any(aspect => aspect is DifferingElement.RemovalAspect or DifferingElement.RemovalHistoryAspect) == true;
    private static string Aspect(DifferingElement item)
    {
      if (item.Aspects == null || IsRemoval(item)) return IsRemoval(item) ? "tu borrado" : null;
      var geometry = item.Aspects.Contains("g"); var parameters = item.Aspects.Contains("p");
      return geometry && parameters ? "la geometría y los parámetros" : geometry ? "la geometría" : parameters ? "los parámetros" : null;
    }
  }
}
