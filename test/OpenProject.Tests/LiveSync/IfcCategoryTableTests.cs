using System.IO;
using System.Text;
using OpenProject.Revit.LiveSync.Bim;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  public class IfcCategoryTableTests
  {
    // Lines copied from C:\ProgramData\Autodesk\RVT 2026\exportlayers-ifc-IAI.txt (Revit in Spanish).
    private static readonly string[] Installed =
    {
      "# Revit IFC Export Mapping File",
      "# Category <tab> Subcategory <tab> Class name <tab> Type name <tab><tab> Category/Subcategory Id",
      "Accesorios de tuberías\t\tIfcValveType\t\t\t-2008055",
      "Aislamientos de conducto\t\tIfcCovering\tINSULATION\tINSULATION\t-2008123",
      "Anotaciones genéricas\t\t\t\t\t-2000150",
      "Mobiliario\t\tIfcFurniture\t\t\t-2000080",
      "Modelos genéricos\t\tIfcBuildingElementProxy\t\t\t-2000151",
      "Modelos genéricos\tIfcOpeningElements\tIfcOpeningElement\t\t\t",
      "Montantes de muro cortina\t\tIfcCurtainWall\t\t\t-2000171",
      ""
    };

    [Fact]
    public void CategoriesAreReadByIdWithTheirClassAndPredefinedType()
    {
      var table = IfcCategoryTable.Parse(Installed);
      Assert.Equal(6, table.Count);
      Assert.True(table.TryGet(-2000080, out var furniture));
      Assert.Equal("IfcFurniture", furniture.Class);
      Assert.True(table.TryGet(-2008123, out var insulation));
      Assert.Equal(("IfcCovering", "INSULATION"), (insulation.Class, insulation.Predefined));
    }

    [Fact]
    public void TypeClassesBecomeTheirOccurrence() =>
      Assert.Equal("IfcValve", Get(-2008055).Class);

    [Fact]
    public void AnEmptyClassMeansTheCategoryIsNotExported()
    {
      var entry = Get(-2000150);
      Assert.Null(entry.Class);
      Assert.Null(entry.Predefined);
    }

    [Fact]
    public void SubcategoryRowsAndCommentsAreSkipped() =>
      Assert.False(IfcCategoryTable.Parse(Installed).TryGet(0, out _));

    [Fact]
    public void FreeTextTypeNamesAreNotTakenAsPredefinedTypes()
    {
      var table = IfcCategoryTable.Parse(new[] { "Muebles\t\tIfcFurniture\tsilla de oficina\t\t-2000080" });
      Assert.True(table.TryGet(-2000080, out var entry));
      Assert.Null(entry.Predefined);
    }

    [Fact]
    public void TheInstalledFileIsReadAsUtf16()
    {
      var path = Path.Combine(Path.GetTempPath(), "ifc-table-" + System.Guid.NewGuid().ToString("N") + ".txt");
      try
      {
        File.WriteAllLines(path, Installed, Encoding.Unicode);
        var table = IfcCategoryTable.Parse(File.ReadAllLines(path));
        Assert.Equal("IfcBuildingElementProxy", Get(table, -2000151).Class);
      }
      finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("DontExport", true)]
    [InlineData("dont export", true)]
    [InlineData("DONT_EXPORT", true)]
    [InlineData("IfcFurniture", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IfcExportAsDontExportExcludesTheElement(string value, bool excluded) =>
      Assert.Equal(excluded, DirectIfcSemantics.IsDontExport(value));

    private static IfcCategoryTable.Entry Get(long id) => Get(IfcCategoryTable.Parse(Installed), id);

    private static IfcCategoryTable.Entry Get(IfcCategoryTable table, long id)
    {
      Assert.True(table.TryGet(id, out var entry));
      return entry;
    }
  }
}
