using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using OpenProject.Revit.LiveSync.Presentation;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  public class SyncHistoryReaderTests
  {
    [Fact]
    public void ReadsRealCountFilteredPagesAndFullRevisionIdentifiers()
    {
      using var folder = new OutboxTests.TestFolder();
      Directory.CreateDirectory(folder.Path);
      using (var db = new SqliteConnection("Data Source=" + Path.Combine(folder.Path, "outbox.sqlite") + ";Pooling=False"))
      {
        db.Open();
        using var table = db.CreateCommand();
        table.CommandText = "CREATE TABLE history (id TEXT PRIMARY KEY,state TEXT,detail TEXT,time TEXT)";
        table.ExecuteNonQuery();
        using var insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO history VALUES ($id,$state,$detail,$time)";
        for (var i = 0; i < 120; i++)
        {
          insert.Parameters.Clear();
          insert.Parameters.AddWithValue("$id", "revision-" + i.ToString("D3"));
          insert.Parameters.AddWithValue("$state", i == 119 ? "Enviando" : i % 2 == 0 ? "Revisión aplicada" : "Guardado en destino");
          insert.Parameters.AddWithValue("$detail", "Cambio " + i);
          insert.Parameters.AddWithValue("$time", DateTime.UtcNow.AddMinutes(i).ToString("O"));
          insert.ExecuteNonQuery();
        }
      }
      var first = SyncHistoryReader.Read(folder.Path, "Todos", 0, 5);
      var second = SyncHistoryReader.Read(folder.Path, "Todos", 5, 5);
      Assert.Equal(120, first.Total);
      Assert.Equal(120, first.Available);
      Assert.Equal("revision-119", first.Items[0].Id);
      Assert.Equal(5, first.Items.Count);
      Assert.Empty(first.Items.Select(e => e.Id).Intersect(second.Items.Select(e => e.Id)));
      Assert.Equal(119, SyncHistoryReader.Read(folder.Path, "Procesados", 0, 10).Total);
      Assert.Equal(0, SyncHistoryReader.Read(folder.Path, "En espera", 0, 10).Total);
      var waiting = SyncHistoryReader.Read(folder.Path, "En espera", 0, 10,
        activeJobId: "revision-119");
      Assert.Single(waiting.Items);
      Assert.Equal("revision-119", waiting.Items[0].Id);
      Assert.True(waiting.Items[0].IsWaiting);
      var historical = SyncHistoryReader.Read(folder.Path, "Todos", 0, 10);
      Assert.Contains(historical.Items, e => e.Id == "revision-117" && !e.IsWaiting &&
        e.Detail.Contains("guardada en CDE"));
      Assert.Single(SyncHistoryReader.Read(folder.Path, "Todos", 0, 10, "revision-011").Items);
      Assert.Empty(SyncHistoryReader.Read(folder.Path, "Todos", 200, 5).Items);
    }
  }
}
