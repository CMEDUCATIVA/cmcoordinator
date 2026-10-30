using System;

namespace OpenProject.Revit.LiveSync.Transport
{
  /// <summary>What the CDE lets this connection do in its model: Lectura, Editor or Administración.</summary>
  /// <remarks>The CDE decides; this only mirrors <c>accessLevel</c>, <c>allowedActions</c> and <c>modelExists</c>.
  /// A CDE without per-model groups sends none of them and keeps the previous behaviour (synchronize allowed).</remarks>
  public sealed class CdeModelAccess
  {
    public static readonly CdeModelAccess Unknown = new(null, null, null, null);
    private readonly string[] _actions;
    public string Level { get; }
    public bool? ModelExists { get; }
    // Account of this connection, shown with its group under the route (UI-8 #27); older servers omit it.
    public string UserName { get; }
    public bool Known => Level != null;
    public bool ReadOnly => Level == "reader";
    public bool CanCreate => Allows("create");
    public bool CanReplace => Allows("replace");
    public bool CanRestore => Allows("restore");
    public bool CanSynchronize => !Known || Allows("sync") || CanCreate;
    public string GroupName => Level switch
    {
      "admin" => "Administración",
      "editor" => "Editor",
      "reader" => "Lectura",
      _ => null
    };
    private CdeModelAccess(string level, string[] actions, bool? modelExists, string userName)
    { Level = level; _actions = actions ?? Array.Empty<string>(); ModelExists = modelExists; UserName = userName; }
    public static CdeModelAccess From(ReceiverStatus status) =>
      status?.AccessLevel is "reader" or "editor" or "admin"
        ? new CdeModelAccess(status.AccessLevel, status.AllowedActions, status.ModelExists, string.IsNullOrWhiteSpace(status.UserName) ? null : status.UserName.Trim()) : Unknown;
    private bool Allows(string action) => Array.IndexOf(_actions, action) >= 0;
    public override bool Equals(object other) => other is CdeModelAccess access && access.Level == Level && access.UserName == UserName &&
      access.ModelExists == ModelExists && string.Join(",", access._actions) == string.Join(",", _actions);
    public override int GetHashCode() => (Level ?? "").GetHashCode() ^ string.Join(",", _actions).GetHashCode();
    public override string ToString() => Known ? GroupName + " · " + string.Join(",", _actions) : "Sin grupos";
  }
}
