#if Version2026 || Version2027
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using OpenProject.Revit.LiveSync.Bim;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  public class IfcNativeSetsTests
  {
    [Fact]
    public void ReadsExportedClassQuantitiesAndCustomSetsByGlobalId()
    {
      // Adapted from ifcquery/ifcplusplus examples/LoadFileExample/example.ifc.
      var assembly = Assembly.GetExecutingAssembly();
      var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("IfcWallExample.ifc", StringComparison.Ordinal));
      var path = Path.Combine(Path.GetTempPath(), "cmcoordinator-ifc-" + Guid.NewGuid().ToString("N") + ".ifc");
      try
      {
        using (var source = assembly.GetManifestResourceStream(resource))
        using (var destination = File.Create(path)) source.CopyTo(destination);
        var elements = IfcNativeSets.ReadElements(path);
        var wall = elements["0Av2L7p1f7EeLe2z5IPn4_"];
        Assert.Equal("IfcWall", wall.IfcClass);
        Assert.Equal("W1", wall.Sets.Single(set => set.Name == "Pset_Wall")
          .Properties.Single(property => property.Name == "WallIentifier").Value);
        Assert.Equal("A1", wall.Sets.Single(set => set.Name == "Custom_Wall")
          .Properties.Single(property => property.Name == "Code").Value);
        var quantity = wall.Sets.Single(set => set.Name == "Qto_WallBaseQuantities")
          .Properties.Single(property => property.Name == "Length");
        Assert.Equal("length", quantity.Type);
        Assert.Equal(5.0, quantity.Value);
      }
      finally { if (File.Exists(path)) File.Delete(path); }
    }
  }
}
#endif
