namespace OpenProject.Revit.Models
{
  public sealed class ProjectOption
  {
    public string Id { get; set; }
    public string Identifier { get; set; }
    public string Name { get; set; }
    public bool IsAll { get; set; }
    public bool IsSearchBox { get; set; }

    public string DisplayName
    {
      get
      {
        if (IsSearchBox)
          return string.Empty;

        if (IsAll)
          return "Todos los proyectos";

        if (!string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Identifier))
          return $"{Name} ({Identifier})";

        if (!string.IsNullOrWhiteSpace(Name))
          return Name;

        if (!string.IsNullOrWhiteSpace(Identifier))
          return Identifier;

        return Id ?? string.Empty;
      }
    }
  }
}
