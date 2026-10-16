using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpenProject.Revit.LiveSync;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  public class LivePacketTests
  {
    private static LiveElement Element(string id) => new() { Id = id, Name = id };
    private static LiveSnapshot Snapshot(params LiveElement[] elements)
    {
      var values = new Dictionary<string, LiveElement>();
      foreach (var element in elements) values[element.Id] = element;
      return new LiveSnapshot("doc", "Modelo", "3D", values);
    }

    [Fact]
    public void ChangesIncludeReplacedElementsAndDeletedIdsOnly()
    {
      var stable = Element("stable");
      var before = Snapshot(stable, Element("changed"), Element("removed"));
      var replacement = Element("changed");
      var after = Snapshot(stable, replacement, Element("added"));
      var delta = LivePacket.Create("session", 2, 1, after, before);
      Assert.False(delta.Reset);
      Assert.Equal(2, delta.Updated.Count);
      Assert.Contains(replacement, delta.Updated);
      Assert.Equal(new[] { "removed" }, delta.Removed);
      Assert.Equal(1, delta.BaseSequence);
    }

    [Fact]
    public void ResetIncludesEmptyModelAndUsesCamelCaseOnWire()
    {
      var reset = LivePacket.Create("session", 1, 0, Snapshot(), null);
      var json = JObject.Parse(LiveJson.Serialize(reset));
      Assert.True(reset.Reset);
      Assert.Empty(reset.Updated);
      Assert.Equal(1, json["protocol"]?.Value<int>());
      Assert.Equal("session", json["sessionId"]?.ToString());
      Assert.NotNull(json["baseSequence"]);
    }

    [Fact]
    public void SnapshotDoesNotShareTheMutableElementDictionary()
    {
      var source = new Dictionary<string, LiveElement> { ["one"] = Element("one") };
      var snapshot = new LiveSnapshot("doc", "Modelo", "3D", source);
      source.Clear();
      Assert.Single(snapshot.Elements);
    }

    [Fact]
    public void ParameterNamesAndDictionaryIdentifiersKeepTheirOriginalCase()
    {
      var element = Element("Upper-ID"); element.Properties["Tipo · ANCHO"] = "1 m";
      var json = JObject.Parse(LiveJson.Serialize(LivePacket.Create("session", 1, 0, Snapshot(element), null)));
      Assert.Equal("1 m", json["updated"][0]["properties"]["Tipo · ANCHO"].ToString());
      Assert.Equal("Upper-ID", json["updated"][0]["id"].ToString());
    }

  }
}
