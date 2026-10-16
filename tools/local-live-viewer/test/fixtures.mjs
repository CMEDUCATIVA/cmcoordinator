export function cube(id = 'element-1') {
  return { id, elementId: '101', name: 'Cubo de 1 m', category: 'Modelo genérico', properties: { Lado: '1 m' },
    meshes: [{ positions: [0,0,0, 1,0,0, 1,1,0, 0,1,0, 0,0,1, 1,0,1, 1,1,1, 0,1,1],
      indices: [0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,1,2,6,1,6,5,2,3,7,2,7,6,3,0,4,3,4,7],
      color: '#8bb8c9', opacity: 1 }] };
}
export function packet(overrides = {}) {
  return { protocol: 1, sessionId: 'session-1', sessionStartedAt: 1000, sourceId: 'doc-1', title: 'Modelo', view: '3D',
    sequence: 1, baseSequence: 0, reset: true, updated: [cube()], removed: [], ...overrides };
}
