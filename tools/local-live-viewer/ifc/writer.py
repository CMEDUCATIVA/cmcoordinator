"""IFC4 coordination snapshot from the local semantic JSON contract (no Revit API)."""
import json
import re
import math
from pathlib import Path
import ifcopenshell
import ifcopenshell.api
import ifcopenshell.validate
import numpy as np
from properties import properties, property_values, stable_guid, text_pset


def api(usecase, model, **kwargs):
    return ifcopenshell.api.run(usecase, model, **kwargs)


def write(snapshot, output):
    context = snapshot.get('bim') or {}
    if context.get('schema') != 2 or not context.get('documents'):
        raise ValueError('Se necesita recapturar con el esquema BIM 2 de Cmcoordinator.')
    model = ifcopenshell.file(schema='IFC4')
    report = {'revision': snapshot['revisionId'], 'sourceId': snapshot['sourceId'], 'scope': context['scope'],
        'scopeId': context['scopeId'], 'schema': 'IFC4', 'representation': 'tessellated', 'elements': 0,
        'typedProperties': 0, 'parametersWithSourceUnitOnly': 0, 'fallbackClasses': [], 'unresolvedRelations': [],
        'limitations': ['Solo elementos capturados de la vista 3D; no equivale a todo el RVT.',
            'Geometría triangulada, sin reconstrucción de perfiles/operaciones paramétricas.',
            'Parámetros originales conservados con definición; unidades no normalizadas se identifican en Definitions.',
            'Coordenadas de geometría en metros del modelo; datos de ubicación de origen conservados sin inventar CRS.',
            'Anfitriones se relacionan por asignación; no se fabrican huecos IFC ausentes.']}
    source = snapshot['sourceId']
    project = api('root.create_entity', model, ifc_class='IfcProject', name=snapshot.get('title', 'Cmcoordinator'))
    project.GlobalId = stable_guid(source + ':project')
    api('unit.assign_unit', model, units=[model.create_entity('IfcSIUnit', UnitType=t, Name=n) for t, n in
        [('LENGTHUNIT', 'METRE'), ('AREAUNIT', 'SQUARE_METRE'), ('VOLUMEUNIT', 'CUBIC_METRE')]])
    geom = api('context.add_context', model, context_type='Model')
    body = api('context.add_context', model, context_type='Model', context_identifier='Body', target_view='MODEL_VIEW', parent=geom)
    site = api('root.create_entity', model, ifc_class='IfcSite', name='Cmcoordinator')
    site.GlobalId = stable_guid(source + ':site')
    def angle(radians):
        degrees = abs(float(radians)) * 180 / math.pi
        d = int(degrees); minutes = (degrees - d) * 60; m = int(minutes)
        sign = -1 if radians < 0 else 1
        return (sign*d, sign*m, sign*int((minutes-m)*60))
    primary = context['documents'][0]
    if 'latitudeRadians' in primary: site.RefLatitude = angle(primary['latitudeRadians'])
    if 'longitudeRadians' in primary: site.RefLongitude = angle(primary['longitudeRadians'])
    api('aggregate.assign_object', model, products=[site], relating_object=project)
    buildings, levels, materials, types, products, styles = {}, {}, {}, {}, {}, {}
    for doc in context['documents']:
        building = api('root.create_entity', model, ifc_class='IfcBuilding', name=doc['title'])
        building.GlobalId = stable_guid(source + doc['id'] + ':building'); buildings[doc['id']] = building
        api('aggregate.assign_object', model, products=[building], relating_object=site)
        properties(model, building, 'Revit_Project', doc['parameters'], report)
        text_pset(model, building, 'Cmcoordinator_Coordinates', {'SourceJson': json.dumps(doc, ensure_ascii=False)})
    for key, level in context['levels'].items():
        item = api('root.create_entity', model, ifc_class='IfcBuildingStorey', name=level['name'])
        item.GlobalId = stable_guid(source + key); item.Elevation = float(level['elevation']); levels[key] = item
        api('aggregate.assign_object', model, products=[item], relating_object=buildings[level['documentId']])
        matrix = np.eye(4); matrix[2, 3] = item.Elevation
        api('geometry.edit_object_placement', model, product=item, matrix=matrix)
    for key, material in context['materials'].items():
        item = model.create_entity('IfcMaterial', Name=material['name'], Category=material.get('category')); materials[key] = item
        # IFC material properties are separate from element property sets.
        props = property_values(model, material['parameters'], report)
        props.append(model.create_entity('IfcPropertySingleValue', Name='SourceJson', NominalValue=model.create_entity('IfcText', json.dumps(material, ensure_ascii=False))))
        model.create_entity('IfcMaterialProperties', Name='Cmcoordinator_Material', Properties=props, Material=item)
    guids = set()
    for element in snapshot['elements']:
        bim = element.get('bim')
        if not bim or bim['documentId'] not in buildings:
            raise ValueError('Elemento sin documento/semántica BIM: ' + element['id'])
        cls = bim['ifcClass']
        try:
            product = api('root.create_entity', model, ifc_class=cls, name=element['name'])
            if not product.is_a('IfcElement'):
                raise ValueError('Clase no física: ' + cls)
        except (RuntimeError, ValueError):
            raise ValueError('Clase IFC no válida para elemento: ' + cls)
        if cls == 'IfcBuildingElementProxy': report['fallbackClasses'].append({'id': element['id'], 'category': element['category']})
        given = bim.get('ifcGuid') or ''
        guid = given if re.fullmatch(r'[0-3][0-9A-Za-z_$]{21}', given) and '/' not in element['id'] else stable_guid(source + element['id'])
        if guid in guids: raise ValueError('IfcGUID duplicado: ' + guid)
        guids.add(guid); product.GlobalId = guid; products[element['id']] = product
        container = levels.get(bim.get('levelId')) or buildings[bim['documentId']]
        api('spatial.assign_container', model, products=[product], relating_structure=container)
        api('geometry.edit_object_placement', model, product=product, matrix=np.eye(4))
        mesh_items = []
        for mesh in element['meshes']:
            vertices = [tuple(map(float, mesh['positions'][i:i+3])) for i in range(0, len(mesh['positions']), 3)]
            faces = [tuple(index + 1 for index in mesh['indices'][i:i+3]) for i in range(0, len(mesh['indices']), 3)]
            coordinates = model.create_entity('IfcCartesianPointList3D', CoordList=vertices)
            mesh_items.append(model.create_entity('IfcTriangulatedFaceSet', Coordinates=coordinates, CoordIndex=faces))
        representation = model.create_entity('IfcShapeRepresentation', ContextOfItems=body,
            RepresentationIdentifier='Body', RepresentationType='Tessellation', Items=mesh_items)
        api('geometry.assign_representation', model, product=product, representation=representation)
        for item, mesh in zip(representation.Items, element['meshes']):
            key = (mesh['color'], mesh['opacity'])
            if key not in styles:
                style = api('style.add_style', model)
                rgb = [int(mesh['color'][i:i+2], 16)/255 for i in (1, 3, 5)]
                api('style.add_surface_style', model, style=style, ifc_class='IfcSurfaceStyleShading',
                    attributes={'SurfaceColour': dict(zip(['Red', 'Green', 'Blue'], rgb)), 'Transparency': 1-float(mesh['opacity'])})
                styles[key] = style
            api('style.assign_item_style', model, item=item, style=styles[key])
        properties(model, product, 'Revit_Instance', bim['parameters'], report)
        text_pset(model, product, 'Cmcoordinator_Identity', {'UniqueId': element['id'], 'ElementId': element['elementId'],
            'Category': element['category'], 'IfcGuidSource': given, 'PhaseId': bim.get('phaseId'),
            'PhaseName': context.get('phases', {}).get(bim.get('phaseId')), 'DocumentId': bim['documentId']})
        type_id = bim.get('typeId'); type_data = context['types'].get(type_id)
        if type_data:
            type_key = (type_id, cls)
            if type_key not in types:
                type_cls = cls + 'Type'
                type_entity = api('root.create_entity', model, ifc_class=type_cls, name=type_data['name'])
                type_entity.GlobalId = stable_guid(source + type_id + cls); types[type_key] = type_entity
                properties(model, type_entity, 'Revit_Type', type_data['parameters'], report)
                text_pset(model, type_entity, 'Cmcoordinator_Type', {'UniqueId': type_id, 'Family': type_data['family'], 'IfcGuidSource': type_data.get('ifcGuid')})
                layers = type_data.get('layers', [])
                if layers:
                    material_set = api('material.add_material_set', model, name=type_data['name'], set_type='IfcMaterialLayerSet')
                    for layer in layers:
                        material = materials.get(layer.get('materialId'))
                        if material is None:
                            material = model.create_entity('IfcMaterial', Name='Sin material asignado')
                        layer_item = api('material.add_layer', model, layer_set=material_set, material=material)
                        layer_item.LayerThickness = float(layer['thickness']); layer_item.Name = layer.get('function')
                    api('material.assign_material', model, products=[type_entity], type='IfcMaterialLayerSet', material=material_set)
            api('type.assign_type', model, related_objects=[product], relating_type=types[type_key], should_map_representations=False)
        assigned = {mesh.get('materialId') for mesh in element['meshes']} - {None}
        if len(assigned) == 1 and next(iter(assigned)) in materials:
            api('material.assign_material', model, products=[product], type='IfcMaterial', material=materials[next(iter(assigned))])
        report['elements'] += 1
    for element in snapshot['elements']:
        host_id = element['bim'].get('hostId')
        if host_id:
            prefix = element['id'].rsplit('/', 1)[0] + '/' if '/' in element['id'] else ''
            host = products.get(prefix + host_id)
            if host:
                model.create_entity('IfcRelAssignsToProduct', GlobalId=stable_guid(source + element['id'] + ':host'), Name='Revit host',
                    RelatedObjects=[products[element['id']]], RelatingProduct=host)
            else: report['unresolvedRelations'].append({'element': element['id'], 'relation': 'host', 'target': host_id})
    text_pset(model, project, 'Cmcoordinator_Revision', {'Revision': snapshot['revisionId'], 'SourceId': source,
        'Scope': context['scope'], 'ScopeId': context['scopeId'], 'Units': 'm', 'UpAxis': 'Z'})
    model.header.file_name.originating_system = 'Cmcoordinator'
    model.header.file_description.description = ('Cmcoordinator coordination snapshot; tessellated geometry',)
    logger = ifcopenshell.validate.json_logger()
    ifcopenshell.validate.validate(model, logger)
    errors = [item for item in logger.statements if item.get('level') == 'error']
    report['validationErrors'] = len(errors)
    if errors: raise ValueError('Validación IFC falló: ' + str(errors[:2]))
    output = Path(output)
    temporary = output.with_suffix('.ifc.tmp'); model.write(str(temporary))
    reopened = ifcopenshell.open(str(temporary))
    if len(reopened.by_type('IfcElement')) != len(snapshot['elements']): raise ValueError('Cantidad de elementos IFC distinta')
    temporary.replace(output)
    return report
