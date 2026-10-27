using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using OpenProject.Revit.LiveSync;
using OpenProject.Revit.LiveSync.Drawings;
using OpenProject.Revit.LiveSync.Queue;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  public class DrawingTests
  {
    private static LiveDrawing Drawing(string view, string svg = "<svg/>") => new()
    {
      Id = LiveDrawing.KeyFor(view), ViewId = view, ElementId = "312", Name = "Planta " + view, ViewType = "FloorPlan", Scale = 100,
      Origin = new double[] { 0, 0, 0 }, Right = new double[] { 1, 0, 0 }, Up = new double[] { 0, 1, 0 }, Direction = new double[] { 0, 0, 1 },
      Bounds = new double[] { 0, 0, 1, 1 }, Elements = new List<string> { "muro-1" }, Svg = svg
    };

    private static LiveSnapshot Snapshot(params LiveDrawing[] drawings) =>
      new LiveSnapshot("doc", "Modelo", "3D", new Dictionary<string, LiveElement>
        { ["muro-1"] = new() { Id = "muro-1", ElementId = "1", Name = "Muro", Category = "Muros" } })
        .WithDrawings(drawings.ToDictionary(d => d.Id));

    private static List<JObject> Parts(SqliteOutbox box, SyncJob job) =>
      Enumerable.Range(0, job.ChunkCount).Select(i => JObject.Parse(Encoding.UTF8.GetString(box.Chunk(i)))).ToList();

    [Fact]
    public void TheWriterGroupsStrokesByElementAndFlipsTheVerticalAxis()
    {
      var writer = new SvgDrawingWriter();
      writer.BeginElement("muro-1", "101", "Muros");
      writer.Line(new[] { (0d, 0d), (1.2, 0d) }, "#000000", 2);
      writer.Line(new[] { (1.2, 0d), (1.2, 3d) }, "#000000", 2);
      writer.EndElement();
      var svg = writer.ToSvg();

      Assert.Contains("<g data-key=\"muro-1\" data-element-id=\"101\" data-category=\"Muros\">", svg);
      // The second stroke continues the first one: a single path, v up becomes y down.
      Assert.Contains("d=\"M0 0L1.2 0L1.2 -3\"", svg);
      Assert.Equal(new[] { "muro-1" }, writer.Keys);
      Assert.Equal(new[] { 0d, 0d, 1.2, 3d }, writer.Bounds);
    }

    [Fact]
    public void AnElementSeenInALaterPassContinuesItsGroup()
    {
      var writer = new SvgDrawingWriter();
      writer.BeginElement("puerta-1", "7", "Puertas");
      writer.Line(new[] { (0d, 0d), (1d, 0d) }, "#000000", 1);
      writer.EndElement();
      writer.BeginElement("puerta-1", "7", "Puertas");
      writer.Text("P-01 & <A>", 0.5, 0.5, 0.0025, 90, "middle", "middle", "#000000", "Arial", false, false);
      writer.EndElement();
      var svg = writer.ToSvg();

      Assert.Single(System.Text.RegularExpressions.Regex.Matches(svg, "data-key=\"puerta-1\""));
      Assert.Contains("P-01 &amp; &lt;A&gt;", svg);
      Assert.Contains("transform=\"rotate(-90 0.5 -0.5)\"", svg);
    }

    [Fact]
    public void FilledRegionsAreClosedShapesWithoutStroke()
    {
      var writer = new SvgDrawingWriter();
      writer.BeginElement("region-1", "9", "Regiones rellenas");
      writer.Fill(new[] { (0d, 0d), (2d, 0d), (2d, 1d) }, "#808080");
      writer.EndElement();

      Assert.Contains("<path d=\"M0 0L2 0L2 -1Z\" fill=\"#808080\" stroke=\"none\"/>", writer.ToSvg());
    }

    [Fact]
    public void ControlCharactersAndUnsafeColoursNeverReachTheSvg()
    {
      var writer = new SvgDrawingWriter();
      writer.BeginElement("t", "1", "Textos");
      writer.Text("a\u0001b", 0, 0, 0.003, 0, "start", "alphabetic", "red;}", "Arial", true, true);
      writer.EndElement();
      var svg = writer.ToSvg();

      Assert.Contains(">ab</text>", svg);
      Assert.Contains("fill=\"#000000\"", svg);
      Assert.Contains("font-weight=\"bold\"", svg);
    }

    [Fact]
    public void TheSelectionSurvivesAndADamagedFileOnlyLosesIt()
    {
      using var folder = new OutboxTests.TestFolder();
      DrawingSelection.Save(folder.Path, new[] { "vista-a", "vista-b", "vista-a" });
      Assert.Equal(new[] { "vista-a", "vista-b" }, DrawingSelection.Load(folder.Path));
      File.WriteAllText(Path.Combine(folder.Path, DrawingSelection.FileName), "{roto");
      Assert.Empty(DrawingSelection.Load(folder.Path));
    }

    [Fact]
    public void WithDrawingsKeepsTheSameCapturedElements()
    {
      var capture = new LiveSnapshot("doc", "Modelo", "3D", new Dictionary<string, LiveElement> { ["muro-1"] = new() { Id = "muro-1" } });
      var drawn = capture.WithDrawings(new Dictionary<string, LiveDrawing> { ["vista-a"] = Drawing("a") });

      Assert.Same(capture.Elements["muro-1"], drawn.Elements["muro-1"]);
      Assert.Null(capture.Drawings);
      Assert.Single(drawn.Drawings);
    }

    [Fact]
    public void DrawingsWaitInTheQueueWhileTheCdeDoesNotAcceptThem()
    {
      using var folder = new OutboxTests.TestFolder();
      using var box = new SqliteOutbox(folder.Path);
      box.Save(Snapshot(Drawing("a")));
      var first = box.Prepare("s", 1, null, true);
      Assert.DoesNotContain(Parts(box, first), part => (string)part["kind"] == "drawings");
      box.Complete(first);

      // Only a drawing changed: without the capability nothing is pending, so no empty revision is published.
      box.Save(Snapshot(Drawing("a", "<svg>2</svg>")));
      Assert.True(box.HasPendingDrawings);
      Assert.False(box.HasPending);
      box.SetDrawingsSupported(true);
      Assert.True(box.HasPending);
    }

    [Fact]
    public void DrawingsTravelInTheSameRevisionAndRemovalsFollow()
    {
      using var folder = new OutboxTests.TestFolder();
      using var box = new SqliteOutbox(folder.Path);
      box.SetDrawingsSupported(true);
      box.Save(Snapshot(Drawing("a"), Drawing("b")));
      var first = box.Prepare("s", 1, null, true, drawings: true);
      var drawings = Parts(box, first).Single(part => (string)part["kind"] == "drawings");
      Assert.Equal(new[] { "vista-a", "vista-b" }, drawings["updated"].Select(d => (string)d["id"]));
      Assert.Equal("<svg/>", (string)drawings["updated"][0]["svg"]);
      Assert.Equal("FloorPlan", (string)drawings["updated"][0]["viewType"]);
      Assert.Empty(drawings["removed"]);
      box.Complete(first);
      Assert.False(box.HasPendingDrawings);

      box.Save(Snapshot(Drawing("a", "<svg>2</svg>")));
      var second = box.Prepare("s", 1, first.JobId, false, drawings: true);
      var next = Parts(box, second).Single(part => (string)part["kind"] == "drawings");
      Assert.Equal(new[] { "vista-a" }, next["updated"].Select(d => (string)d["id"]));
      Assert.Equal(new[] { "vista-b" }, next["removed"].Select(id => (string)id));
    }

    [Fact]
    public void AnAbandonedRevisionSendsItsDrawingsAgain()
    {
      using var folder = new OutboxTests.TestFolder();
      using var box = new SqliteOutbox(folder.Path);
      box.SetDrawingsSupported(true);
      box.Save(Snapshot(Drawing("a")));
      box.Prepare("s", 1, null, true, drawings: true);
      box.Abandon("El CDE rechazó el contenido");

      Assert.True(box.HasPendingDrawings);
      var retry = box.Prepare("s", 1, null, true, drawings: true);
      Assert.Contains(Parts(box, retry), part => (string)part["kind"] == "drawings");
    }
  }
}
