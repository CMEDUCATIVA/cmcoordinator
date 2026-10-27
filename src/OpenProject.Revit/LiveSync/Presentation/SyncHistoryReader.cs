using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

[assembly: InternalsVisibleTo("OpenProject.Tests")]

namespace OpenProject.Revit.LiveSync.Presentation
{
  // Read-only view of the active document's retained outbox history.
  internal static class SyncHistoryReader
  {
    internal sealed class Page
    {
      public int Total { get; init; }
      public int Available { get; init; }
      public List<Entry> Items { get; init; } = new();
    }

    internal sealed class Entry
    {
      public string Id { get; init; }
      public string State { get; init; }
      public string Detail { get; init; }
      public bool IsWaiting { get; init; }
      public DateTime LocalTime { get; init; }
      public string Clock => LocalTime.ToString("HH:mm:ss");
      public string DateAndTime => LocalTime.ToString("dd/MM/yyyy HH:mm:ss");
    }

    internal static Page Read(string directory, string filter, int offset, int limit,
      string revision = null, DateTime? from = null, DateTime? to = null,
      string activeJobId = null)
    {
      var path = directory == null ? null : Path.Combine(directory, "outbox.sqlite");
      if (path == null || !File.Exists(path)) return new Page();
      using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
      db.Open();
      using var availableCount = db.CreateCommand();
      availableCount.CommandText = "SELECT count(*) FROM history";
      var available = Convert.ToInt32(availableCount.ExecuteScalar());
      var where = " WHERE 1=1";
      if (filter == "En espera") where += " AND id=$active AND state NOT IN ('Reconciliado','Revisión aplicada','Guardado en destino')";
      if (filter == "Procesados") where += " AND state IN ('Reconciliado','Revisión aplicada','Guardado en destino')";
      if (!string.IsNullOrWhiteSpace(revision)) where += " AND instr(lower(id),lower($revision))>0";
      if (from.HasValue) where += " AND time >= $from";
      if (to.HasValue) where += " AND time < $to";
      using var count = db.CreateCommand();
      count.CommandText = "SELECT count(*) FROM history" + where;
      AddFilters(count, revision, from, to, activeJobId);
      var total = Convert.ToInt32(count.ExecuteScalar());
      using var rows = db.CreateCommand();
      rows.CommandText = "SELECT id,state,detail,time FROM history" + where + " ORDER BY time DESC,id DESC LIMIT $limit OFFSET $offset";
      AddFilters(rows, revision, from, to, activeJobId);
      rows.Parameters.AddWithValue("$limit", NetCompat.Clamp(limit, 1, 50));
      rows.Parameters.AddWithValue("$offset", Math.Max(0, offset));
      var result = new Page { Total = total, Available = available };
      using var reader = rows.ExecuteReader();
      while (reader.Read())
      {
        var id = reader.GetString(0);
        var state = reader.IsDBNull(1) ? "Sin estado" : reader.GetString(1);
        var waiting = id == activeJobId && state is not ("Reconciliado" or "Revisión aplicada" or "Guardado en destino");
        var detail = reader.IsDBNull(2) ? "" : reader.GetString(2);
        if (state == "Guardado en destino") detail = "Revisión recibida y guardada en CDE.";
        else if (!waiting && state is ("En cola" or "Enviando" or "Validando revisión"))
          detail = "Operación anterior; esta revisión ya no se está procesando.";
        result.Items.Add(new Entry
        {
          Id = id, State = state, Detail = detail, IsWaiting = waiting,
          LocalTime = DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToLocalTime()
        });
      }
      return result;
    }

    private static void AddFilters(SqliteCommand command, string revision, DateTime? from, DateTime? to,
      string activeJobId)
    {
      command.Parameters.AddWithValue("$active", activeJobId ?? "");
      if (!string.IsNullOrWhiteSpace(revision)) command.Parameters.AddWithValue("$revision", revision.Trim());
      if (from.HasValue) command.Parameters.AddWithValue("$from", from.Value.ToUniversalTime().ToString("O"));
      if (to.HasValue) command.Parameters.AddWithValue("$to", to.Value.ToUniversalTime().ToString("O"));
    }
  }
}
