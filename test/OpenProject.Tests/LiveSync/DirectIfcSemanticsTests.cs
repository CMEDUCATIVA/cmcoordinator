using OpenProject.Revit.LiveSync.Bim;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  public class DirectIfcSemanticsTests
  {
    [Theory]
    [InlineData("IfcBeam", "Qto_BeamBaseQuantities")]
    [InlineData("IfcColumn", "Qto_ColumnBaseQuantities")]
    [InlineData("IfcMember", "Qto_MemberBaseQuantities")]
    public void StraightStructuralMembersHaveIfc4Length(string ifcClass, string setName)
    {
      var set = DirectIfcSemantics.StraightLength(ifcClass, 10);
      Assert.Equal(setName, set.Name);
      Assert.Equal("qto", set.Kind);
      Assert.Equal("Length", set.Properties[0].Name);
      Assert.Equal(3.048, (double)set.Properties[0].Value, 6);
      Assert.Equal("metre", set.Properties[0].Unit);
    }

    [Theory]
    [InlineData("IfcSlab")]
    [InlineData("IfcBuildingElementProxy")]
    [InlineData("IfcBeamType")]
    public void UnsupportedClassesDoNotReceiveAnInventedQuantity(string ifcClass) =>
      Assert.Null(DirectIfcSemantics.StraightLength(ifcClass, 10));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonpositiveOrNonfiniteLengthsAreOmitted(double feet) =>
      Assert.Null(DirectIfcSemantics.StraightLength("IfcBeam", feet));

    [Theory]
    [InlineData(" IfcBeam ", "IfcBeam")]
    [InlineData("Not Exported", null)]
    [InlineData("IfcBeam;delete", null)]
    public void ExportClassOverridesMustBeEntityShaped(string value, string expected) =>
      Assert.Equal(expected, DirectIfcSemantics.ClassName(value));

    [Fact]
    public void SlabNetVolumeUsesCubicMetres()
    {
      var set = DirectIfcSemantics.SlabNetVolume(100);
      Assert.Equal("Qto_SlabBaseQuantities", set.Name);
      Assert.Equal("NetVolume", set.Properties[0].Name);
      Assert.Equal(2.8316846592, (double)set.Properties[0].Value, 8);
      Assert.Equal("cubic_metre", set.Properties[0].Unit);
    }

    [Theory]
    [InlineData("IfcBuildingElementProxy", "IfcBeam", "IfcBeam")]
    [InlineData("IfcWall", "IfcBeam", "IfcWall")]
    [InlineData(null, "IfcSlab", "IfcSlab")]
    [InlineData("IfcFlowTerminal", "IfcSanitaryTerminal", "IfcSanitaryTerminal")]
    [InlineData("IfcFlowSegment", "IfcPipeSegment", "IfcPipeSegment")]
    [InlineData("IfcFlowTerminal", "IfcFurniture", "IfcFlowTerminal")]
    [InlineData("IfcBuildingElementProxy", "IfcBuildingElementProxy", "IfcBuildingElementProxy")]
    public void TypedCategoryWinsOnlyOverGenericProxy(string mapped, string category, string expected) =>
      Assert.Equal(expected, DirectIfcSemantics.PreferTypedClass(mapped, category));

    [Theory]
    [InlineData("IfcSlab", "Qto_SlabBaseQuantities", "Pset_SlabCommon")]
    [InlineData("IfcWall", "Qto_WallBaseQuantities", "Pset_WallCommon")]
    [InlineData("IfcBuildingElementProxy", null, "Pset_BuildingElementProxyCommon")]
    [InlineData("IfcCurtainWall", null, "Pset_CurtainWallCommon")]
    [InlineData("IfcGeographicElement", null, null)]
    public void StandardSetNamesAreResolvedByIfcClass(string ifcClass, string qto, string pset)
    {
      Assert.Equal(qto, DirectIfcSemantics.BaseQuantitySet(ifcClass));
      Assert.Equal(pset, DirectIfcSemantics.CommonPropertySet(ifcClass));
    }

    [Fact]
    public void AreaUsesSquareMetres()
    {
      var property = DirectIfcSemantics.Area("NetArea", 100);
      Assert.Equal(9.290304, (double)property.Value, 6);
      Assert.Equal("square_metre", property.Unit);
      Assert.Equal("area", property.Type);
    }

    [Theory]
    [InlineData(false, "Cotas", null, "Instancia · Cotas")]
    [InlineData(true, "Construcción", "Generic 300mm", "Tipo · Construcción · Generic 300mm")]
    [InlineData(false, " ", null, "Instancia · Otros")]
    public void AdditionalGroupsUseNeutralVisibleNames(bool isType, string group, string typeName, string expected) =>
      Assert.Equal(expected, DirectIfcSemantics.AdditionalSetName(isType, group, typeName));

    [Theory]
    [InlineData("IfcLightFixtureType", "IfcLightFixture")]
    [InlineData("IfcSanitaryTerminalType", "IfcSanitaryTerminal")]
    [InlineData("IfcDoorStyle", "IfcDoor")]
    [InlineData("IfcWall", "IfcWall")]
    [InlineData("IfcTypeProduct", "IfcTypeProduct")]
    public void TypeObjectsAreReplacedByTheirOccurrenceClass(string mapped, string expected) =>
      Assert.Equal(expected, DirectIfcSemantics.OccurrenceClass(mapped));

    [Theory]
    [InlineData("IfcSlab.ROOF", "IfcSlab", "ROOF")]
    [InlineData(" IfcLightFixtureType ", "IfcLightFixture", null)]
    [InlineData("IfcSlab.losa especial", "IfcSlab", null)]
    [InlineData("Not Exported", null, null)]
    public void ExportAsAcceptsPredefinedTypeSuffix(string value, string ifcClass, string predefined)
    {
      var parsed = DirectIfcSemantics.ParseEntity(value);
      Assert.Equal(ifcClass, parsed.Class);
      Assert.Equal(predefined, parsed.Predefined);
    }

    [Theory]
    [InlineData("OST_CurtainWallMullions", "IfcMember", "MULLION")]
    [InlineData("OST_CurtainWallPanels", "IfcPlate", "CURTAIN_PANEL")]
    [InlineData("OST_Topography", "IfcGeographicElement", "TERRAIN")]
    [InlineData("OST_Toposolid", "IfcGeographicElement", "TERRAIN")]
    [InlineData("OST_Planting", "IfcGeographicElement", "VEGETATION")]
    [InlineData("OST_StairsLandings", "IfcSlab", "LANDING")]
    [InlineData("OST_StairsRuns", "IfcStairFlight", null)]
    [InlineData("OST_StairsStringerCarriage", "IfcMember", "STRINGER")]
    [InlineData("OST_StructuralFramingSystem", "IfcElementAssembly", "BEAM_GRID")]
    public void CurtainWallPartsAndSiteElementsFollowBuildingSmart(string category, string ifcClass, string predefined)
    {
      var rule = DirectIfcSemantics.NormativeCategory(category);
      Assert.NotNull(rule);
      Assert.Equal(ifcClass, rule.Value.Class);
      Assert.Equal(predefined, rule.Value.Predefined);
    }

    [Fact]
    public void OrdinaryCategoriesKeepTheProjectTemplate() =>
      Assert.Null(DirectIfcSemantics.NormativeCategory("OST_Walls"));

    [Fact]
    public void SiteIsTheSpatialContainerNotAPhysicalElement()
    {
      Assert.Equal("IfcGeographicElement", DirectIfcSemantics.NormalizeSite("IfcSite"));
      Assert.Equal("IfcWall", DirectIfcSemantics.NormalizeSite("IfcWall"));
    }

    [Theory]
    [InlineData("ROOF", "ROOF", null)]
    [InlineData("Losa de cubierta", "USERDEFINED", "Losa de cubierta")]
    [InlineData(" ", null, null)]
    public void FreeTextPredefinedTypeBecomesUserDefinedWithObjectType(string value, string expected, string objectType)
    {
      Assert.Equal(expected, DirectIfcSemantics.PredefinedToken(value, out var actualObjectType));
      Assert.Equal(objectType, actualObjectType);
    }

    [Theory]
    [InlineData("OST_Floors", "IfcSlab", "FLOOR")]
    [InlineData("OST_Ceilings", "IfcCovering", "CEILING")]
    [InlineData("OST_StructuralFraming", "IfcBeam", "BEAM")]
    [InlineData("OST_StructuralFraming", "IfcMember", null)]
    public void DefaultPredefinedTypesMatchTheAutodeskExporter(string category, string ifcClass, string expected) =>
      Assert.Equal(expected, DirectIfcSemantics.DefaultPredefinedType(category, ifcClass));

    [Fact]
    public void VerticalColumnLengthRunsBetweenConstrainedLevels()
    {
      var length = DirectIfcSemantics.VerticalLength(0, 1, 10, -1);
      Assert.Equal(8 * 0.3048, (double)length.Value, 6);
      Assert.Equal("metre", length.Unit);
      Assert.Null(DirectIfcSemantics.VerticalLength(10, 0, 10, 0));
      Assert.Null(DirectIfcSemantics.VerticalLength(null, 0, 10, 0));
    }
  }
}
