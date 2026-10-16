using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace OpenProject.Revit.LiveSync
{
  public sealed class LiveMesh
  {
    public double[] Positions { get; set; } = Array.Empty<double>();
    public int[] Indices { get; set; } = Array.Empty<int>();
    public string Color { get; set; } = "#8bb8c9";
    public double Opacity { get; set; } = 1;
    public string MaterialId { get; set; }
  }

  public sealed class LiveElement
  {
    public string Id { get; set; }
    public string ElementId { get; set; }
    public string Name { get; set; }
    public string Category { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new();
    public List<LiveMesh> Meshes { get; set; } = new();
    public Bim.BimElement Bim { get; set; }
  }

  public sealed class LiveSnapshot
  {
    public LiveSnapshot(string sourceId, string title, string view, IDictionary<string, LiveElement> elements, Bim.BimContext bim = null)
    {
      SourceId = sourceId;
      Title = title;
      View = view;
      Elements = new Dictionary<string, LiveElement>(elements, StringComparer.Ordinal);
      Bim = bim;
    }

    public string SourceId { get; }
    public string Title { get; }
    public string View { get; }
    public IReadOnlyDictionary<string, LiveElement> Elements { get; }
    public Bim.BimContext Bim { get; }
  }

  public sealed class LivePacket
  {
    public int Protocol { get; set; } = 1;
    public string SessionId { get; set; }
    public long SessionStartedAt { get; set; }
    public string SourceId { get; set; }
    public string Title { get; set; }
    public string View { get; set; }
    public long Sequence { get; set; }
    public long BaseSequence { get; set; }
    public bool Reset { get; set; }
    public List<LiveElement> Updated { get; set; }
    public List<string> Removed { get; set; }
    public Bim.BimContext Bim { get; set; }

    public static LivePacket Create(string sessionId, long sequence, long baseSequence,
      LiveSnapshot current, LiveSnapshot acknowledged, long sessionStartedAt = 1)
    {
      var reset = acknowledged == null || acknowledged.SourceId != current.SourceId;
      return new LivePacket
      {
        SessionId = sessionId, SessionStartedAt = sessionStartedAt, Sequence = sequence, BaseSequence = baseSequence,
        SourceId = current.SourceId, Title = current.Title, View = current.View, Reset = reset, Bim = current.Bim,
        Updated = current.Elements.Where(kv => reset ||
          !acknowledged.Elements.TryGetValue(kv.Key, out var previous) ||
          !ReferenceEquals(previous, kv.Value)).Select(kv => kv.Value).ToList(),
        Removed = reset ? new List<string>() : acknowledged.Elements.Keys
          .Where(id => !current.Elements.ContainsKey(id)).ToList()
      };
    }
  }

  public static class LiveJson
  {
    public static readonly JsonSerializerSettings Settings = new()
    {
      ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver
      {
        NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy
          { ProcessDictionaryKeys = false, OverrideSpecifiedNames = false }
      },
      FloatFormatHandling = FloatFormatHandling.DefaultValue
    };

    public static string Serialize(LivePacket packet) => JsonConvert.SerializeObject(packet, Settings);
  }
}
