import { cube, packet } from './fixtures.mjs';
export function bimPacket() {
  const parameter = { id: '42', name: 'Altura', storage: 'Double', value: 3 / 0.3048,
    siValue: 3, displayUnitValue: 3000, display: '3000 mm', unitId: 'autodesk.unit.unit:millimeters-1.0.1',
    specId: 'autodesk.spec.aec:length-2.0.0', hasValue: true, readOnly: false, sharedGuid: null, referenceId: null };
  const element = cube();
  element.bim = { documentId: 'doc', ifcGuid: '0ABCDEFGHIJKLMNOPQRSTU', ifcClass: 'IfcWall', typeId: 'type',
    levelId: 'level', hostId: null, phaseId: 'phase', categoryId: '-2000011', parameters: [parameter,
      { id: '43', name: 'Código', storage: 'String', value: 'Ñ-001', display: 'Ñ-001', hasValue: true },
      { id: '44', name: 'Sin valor', storage: 'String', value: null, display: null, hasValue: false }] };
  element.meshes[0].materialId = 'material';
  return packet({ updated: [element], bim: { schema: 2, scope: 'visible-3d', scopeId: 'view',
    documents: [{ id: 'doc', title: 'Edificio de prueba', number: '001', projectPosition: [0, 0, 0, 0], parameters: [] }],
    levels: { level: { id: 'level', documentId: 'doc', name: 'Nivel 1', elevation: 0 } },
    types: { type: { id: 'type', documentId: 'doc', name: 'Muro 200', family: 'Muro básico', parameters: [{ ...parameter, id: 'width', name: 'Espesor', siValue: 0.2 }], layers: [{ materialId: 'material', thickness: 0.2, function: 'Structure' }] } },
    materials: { material: { id: 'material', name: 'Hormigón', color: '#8bb8c9', opacity: 1, category: 'Estructura',
      parameters: [{ id: 'yesno', name: 'Estructural', storage: 'Integer', specId: 'autodesk.spec:spec.bool-1.0.0', value: 1, hasValue: true }] } } } });
}
