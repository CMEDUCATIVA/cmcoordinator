"""Typed IFC properties without discarding original Revit definitions or units."""
import hashlib
import json
import ifcopenshell.guid


def stable_guid(key):
    return ifcopenshell.guid.compress(hashlib.sha256(key.encode()).hexdigest()[:32])


def property_values(model, parameters, report):
    values = []
    for p in parameters:
        value, unit, measure = p.get('value'), None, None
        if p.get('hasValue') and value is not None:
            if p['storage'] == 'Double':
                value = float(p.get('siValue') if p.get('siValue') is not None else value)
                for spec, ifc_measure, unit_type, unit_name in [
                    (':length-', 'IfcLengthMeasure', 'LENGTHUNIT', 'METRE'),
                    (':area-', 'IfcAreaMeasure', 'AREAUNIT', 'SQUARE_METRE'),
                    (':volume-', 'IfcVolumeMeasure', 'VOLUMEUNIT', 'CUBIC_METRE'),
                    (':angle-', 'IfcPlaneAngleMeasure', 'PLANEANGLEUNIT', 'RADIAN')]:
                    if spec in (p.get('specId') or '') and p.get('siValue') is not None:
                        measure = ifc_measure
                        unit = model.create_entity('IfcSIUnit', UnitType=unit_type, Name=unit_name)
                        break
                if measure is None and p.get('unitId'):
                    report['parametersWithSourceUnitOnly'] += 1
                nominal = model.create_entity(measure or 'IfcReal', value)
            elif p['storage'] == 'Integer':
                spec = (p.get('specId') or '').lower()
                nominal = model.create_entity('IfcBoolean', bool(value)) if any(tag in spec for tag in ['boolean', ':bool', '.bool', 'yesno']) else model.create_entity('IfcInteger', int(value))
            else:
                nominal = model.create_entity('IfcText', str(value))
        else:
            nominal = None
        values.append(model.create_entity('IfcPropertySingleValue', Name=f"{p['name']} [{p['id']}]",
            Description=p.get('display'), NominalValue=nominal, Unit=unit))
    report['typedProperties'] += len(values)
    return values


def properties(model, product, name, parameters, report):
    values = property_values(model, parameters, report)
    if values:
        pset = model.create_entity('IfcPropertySet', GlobalId=stable_guid(product.GlobalId + name), Name=name, HasProperties=values)
        attach(model, product, pset)
    # Definitions retain native values, shared GUIDs, spec/unit IDs, references and unset flags.
    text_pset(model, product, name + '_Definitions', {'ParametersJson': json.dumps(parameters, ensure_ascii=False, allow_nan=False)})


def text_pset(model, product, name, items):
    values = [model.create_entity('IfcPropertySingleValue', Name=str(k), NominalValue=model.create_entity('IfcText', str(v)))
              for k, v in items.items() if v is not None]
    if not values:
        return
    pset = model.create_entity('IfcPropertySet', GlobalId=stable_guid(product.GlobalId + name), Name=name, HasProperties=values)
    attach(model, product, pset)


def attach(model, product, pset):
    if product.is_a('IfcTypeObject'):
        product.HasPropertySets = list(product.HasPropertySets or []) + [pset]
    else:
        model.create_entity('IfcRelDefinesByProperties', GlobalId=stable_guid(pset.GlobalId + ':rel'), RelatedObjects=[product], RelatingPropertyDefinition=pset)
