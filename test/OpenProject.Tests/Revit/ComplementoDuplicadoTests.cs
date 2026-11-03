using System.IO;
using System.Linq;
using OpenProject.Revit.Entry;
using Xunit;

namespace OpenProject.Tests.Revit
{
  public class ComplementoDuplicadoTests
  {
    private const string Actual = "b3ca797e-98df-4670-92e9-7b8d19c5c695";
    private const string Antiguo = "5f96a79f-0e28-4d02-be10-251c8032a270";

    private static string Manifiesto(string id, string ensamblado) =>
      $"<RevitAddIns><AddIn Type=\"Application\"><Assembly>{ensamblado}</Assembly><AddInId>{id}</AddInId></AddIn></RevitAddIns>";

    [Fact]
    public void FindsOtherCopiesWithEitherIdentifierButNotItselfNorOtherAddIns()
    {
      var raiz = Directory.CreateTempSubdirectory("cmc-dup").FullName;
      try
      {
        var programData = Directory.CreateDirectory(Path.Combine(raiz, "ProgramData", "2027")).FullName;
        var programFiles = Directory.CreateDirectory(Path.Combine(raiz, "ProgramFiles", "2027")).FullName;
        var propia = Path.Combine(programFiles, "Cmcoordinator", "OpenProject.Revit.dll");
        File.WriteAllText(Path.Combine(programFiles, "Cmcoordinator.addin"), Manifiesto(Actual, @"Cmcoordinator\OpenProject.Revit.dll"));
        File.WriteAllText(Path.Combine(programData, "Cmcoordinator.addin"), Manifiesto(Actual, @"Cmcoordinator\OpenProject.Revit.dll"));
        File.WriteAllText(Path.Combine(programData, "OpenProject.Revit.addin"), Manifiesto(Antiguo, @"OpenProject.Revit\OpenProject.Revit.dll"));
        File.WriteAllText(Path.Combine(programData, "Otro.addin"), Manifiesto("11111111-2222-3333-4444-555555555555", @"Otro\Otro.dll"));

        var otras = ComplementoDuplicado.Buscar(new[] { programData, programFiles, Path.Combine(raiz, "falta") }, propia);

        Assert.Equal(new[] { "Cmcoordinator.addin", "OpenProject.Revit.addin" }, otras.ConvertAll(Path.GetFileName).OrderBy(n => n).ToArray());
        Assert.All(otras, ruta => Assert.StartsWith(programData, ruta));
      }
      finally { Directory.Delete(raiz, true); }
    }
  }
}
