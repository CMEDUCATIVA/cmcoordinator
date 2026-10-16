"""Independent fixture roundtrip assertions using the IFC geometry engine."""
import json
import sys
import ifcopenshell
import ifcopenshell.geom
import ifcopenshell.util.element

model = ifcopenshell.open(sys.argv[1])
product = model.by_type('IfcWall')[0]
pset = ifcopenshell.util.element.get_psets(product)['Revit_Instance']
shape = ifcopenshell.geom.create_shape(ifcopenshell.geom.settings(), product)
vertices = list(shape.geometry.verts)
minimum = [min(vertices[i::3]) for i in range(3)]
maximum = [max(vertices[i::3]) for i in range(3)]
result = {'elements': len(model.by_type('IfcElement')), 'triangles': len(shape.geometry.faces) // 3,
    'guid': product.GlobalId, 'height': pset['Altura [42]'], 'codes': pset['Código [43]'],
    'level': ifcopenshell.util.element.get_container(product).Name,
    'type': ifcopenshell.util.element.get_type(product).Name, 'materials': len(model.by_type('IfcMaterial')),
    'boundsMatchUnitCube': all(abs(minimum[i]) < 1e-6 and abs(maximum[i] - 1) < 1e-6 for i in range(3))}
material_property = next(p for group in model.by_type('IfcMaterialProperties') for p in group.Properties if p.Name == 'Estructural [yesno]')
result['materialBoolean'] = material_property.NominalValue.is_a('IfcBoolean') and material_property.NominalValue.wrappedValue
print(json.dumps(result, ensure_ascii=True))
