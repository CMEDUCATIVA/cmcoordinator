// Network assembly, JSON decoding and normals stay off the browser's presentation thread.
const hex = bytes => [...new Uint8Array(bytes)].map(value => value.toString(16).padStart(2, '0')).join('');
const hash = async bytes => hex(await crypto.subtle.digest('SHA-256', bytes));

function normals(positions, indices) {
  const result = new Float32Array(positions.length);
  for (let i = 0; i < indices.length; i += 3) {
    const a = indices[i] * 3, b = indices[i + 1] * 3, c = indices[i + 2] * 3;
    const ux = positions[b] - positions[a], uy = positions[b + 1] - positions[a + 1], uz = positions[b + 2] - positions[a + 2];
    const vx = positions[c] - positions[a], vy = positions[c + 1] - positions[a + 1], vz = positions[c + 2] - positions[a + 2];
    const nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
    for (const offset of [a, b, c]) { result[offset] += nx; result[offset + 1] += ny; result[offset + 2] += nz; }
  }
  for (let i = 0; i < result.length; i += 3) {
    const length = Math.hypot(result[i], result[i + 1], result[i + 2]) || 1;
    result[i] /= length; result[i + 1] /= length; result[i + 2] /= length;
  }
  return result;
}

self.onmessage = async event => {
  try {
    let { manifest } = event.data;
    const { mode } = event.data;
    if (mode === 'base' && !manifest.baseReady) {
      self.postMessage({ kind: 'progress', stage: 'Preparando base', done: 0, total: 0 });
      const response = await fetch(`/api/v2/revisions/${manifest.revisionId}`);
      if (!response.ok) throw new Error(`La base cambió o no está disponible: HTTP ${response.status}`);
      manifest = await response.json();
    }
    const info = mode === 'delta' ? manifest.delta : manifest;
    if (!info || info.byteLength > 34 * 1024 * 1024 || info.partCount > 68) throw new Error('Revisión fuera de los límites del visor');
    const bytes = new Uint8Array(info.byteLength); let offset = 0;
    for (let i = 0; i < info.partCount; i++) {
      const response = await fetch(`/api/v2/revisions/${manifest.revisionId}/parts/${i}?mode=${mode}`);
      if (!response.ok) throw new Error(`Parte no disponible: HTTP ${response.status}`);
      const part = await response.arrayBuffer();
      if (await hash(part) !== response.headers.get('X-Chunk-Sha256')) throw new Error('Checksum de parte incorrecto');
      bytes.set(new Uint8Array(part), offset); offset += part.byteLength;
      self.postMessage({ kind: 'progress', stage: 'Recibiendo', done: i + 1, total: info.partCount });
    }
    if (offset !== bytes.length || await hash(bytes) !== info.payloadHash) throw new Error('Checksum de revisión incorrecto');
    const snapshot = JSON.parse(new TextDecoder().decode(bytes));
    let origin = event.data.origin ?? [0, 0, 0];
    if (mode === 'base') {
      const min = [Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity];
      for (const element of snapshot.elements) for (const mesh of element.meshes) for (let i = 0; i < mesh.positions.length; i++) {
        const axis = i % 3; min[axis] = Math.min(min[axis], mesh.positions[i]); max[axis] = Math.max(max[axis], mesh.positions[i]);
      }
      origin = min.map((value, i) => Number.isFinite(value) ? (value + max[i]) / 2 : 0);
    }
    self.postMessage({ kind: 'begin', origin, removed: snapshot.removed ?? [], mode, bim: snapshot.bim });
    let batch = [], transfer = [];
    for (const element of snapshot.elements) {
      for (const mesh of element.meshes) {
        const source = mesh.positions;
        mesh.positions = Float32Array.from(source, (value, i) => value - origin[i % 3]);
        mesh.indices = Uint32Array.from(mesh.indices);
        mesh.normals = normals(mesh.positions, mesh.indices);
        transfer.push(mesh.positions.buffer, mesh.indices.buffer, mesh.normals.buffer);
      }
      batch.push(element);
      if (batch.length === 12) { self.postMessage({ kind: 'elements', elements: batch }, transfer); batch = []; transfer = []; }
    }
    if (batch.length) self.postMessage({ kind: 'elements', elements: batch }, transfer);
    self.postMessage({ kind: 'end' });
  } catch (error) { self.postMessage({ kind: 'error', message: error.message }); }
};
