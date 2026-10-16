import { ModelRenderer } from './renderer.js';
import { SceneState } from './scene-state.js';
import { RevisionLoader } from './revision-loader.js';
import { inspectBim } from './bim-inspector.js';
import { setupIfcExport } from './ifc-export.js';

const $ = id => document.getElementById(id);
const state = new SceneState();
let selected = null, renderer, stream, revisions;
let currentSnapshot = null;
const updateIfc = setupIfcExport(() => currentSnapshot);

function connection(text, mode = '') {
  $('connection').textContent = text;
  $('connection').className = `badge ${mode}`;
}

function inspect(id) {
  selected = id;
  const element = state.elements.get(id);
  $('selection').textContent = element?.name ?? 'Propiedades del elemento';
  $('selection-empty').hidden = !!element;
  $('properties').replaceChildren();
  if (!element) return;
  inspectBim($('properties'), element, state.bim);
}

function refresh() {
  $('empty').hidden = state.elements.size > 0;
  $('count').textContent = state.elements.size.toLocaleString('es');
  let triangles = 0;
  for (const element of state.elements.values()) for (const mesh of element.meshes) triangles += mesh.indices.length / 3;
  $('triangles').textContent = triangles.toLocaleString('es');
  $('sequence').textContent = state.sequence;
  $('source').textContent = state.title ?? 'Esperando a Revit';
  $('view').textContent = state.view ? `Vista: ${state.view} · metros · Z vertical` : 'Solo en este equipo · metros · eje Z vertical';
  $('updated').textContent = state.updatedAt ? `Última actualización: ${new Date(state.updatedAt).toLocaleTimeString('es')}` : 'Sin actualizaciones';
  if (selected) inspect(selected);
}

function subscribe() {
  stream?.close();
  stream = new EventSource('/events');
  stream.onopen = () => connection('Visor conectado · esperando cambios', 'connected');
  stream.onerror = () => connection('Sin conexión · reintentando…', 'error');
  stream.onmessage = event => {
    try {
      const message = JSON.parse(event.data);
      if (message.kind === 'revision') { revisions.load(message.revision); return; }
      if (message.kind === 'snapshot') {
        state.snapshot(message.state);
        renderer.reset([...state.elements.values()]);
      } else if (message.kind === 'patch') {
        const packet = message.packet;
        if (!state.patch(packet)) { subscribe(); return; }
        if (packet.reset) renderer.reset([...state.elements.values()]);
        else {
          for (const id of packet.removed) renderer.remove(id);
          for (const element of packet.updated) renderer.replace(element);
        }
      }
      refresh();
      connection(state.sessionId ? 'Modelo recibido · visor conectado' : 'Visor listo · esperando a Revit', 'connected');
    } catch (error) {
      connection(`Error: ${error.message}`, 'error');
      stream.close();
    }
  };
}

try {
  renderer = new ModelRenderer($('scene'), inspect);
  revisions = new RevisionLoader(renderer, {
    progress: (stage, done, total) => {
      $('load-progress').hidden = false;
      $('load-stage').textContent = `${stage} · ${done}/${total}`;
      $('load-bar').max = Math.max(total, 1); $('load-bar').value = done;
    },
    applied: snapshot => {
      currentSnapshot = snapshot; updateIfc();
      state.snapshot(snapshot); refresh();
      $('load-progress').hidden = true;
      connection('Revisión aplicada · visor conectado', 'connected');
    },
    error: message => { $('load-progress').hidden = true; connection(`Carga pendiente: ${message}`, 'error'); }
  });
  $('fit').addEventListener('click', () => renderer.fit());
  $('grid').addEventListener('click', () => {
    renderer.grid.visible = !renderer.grid.visible;
    $('grid').setAttribute('aria-pressed', String(renderer.grid.visible));
    renderer.render();
  });
  window.addEventListener('keydown', event => { if (event.key.toLowerCase() === 'f') renderer.fit(); });
  window.addEventListener('beforeunload', () => { stream?.close(); revisions.cancel?.(); });
  subscribe();
} catch (error) {
  connection('No se pudo iniciar WebGL', 'error');
  $('empty').textContent = `No se pudo iniciar el visor: ${error.message}`;
}
