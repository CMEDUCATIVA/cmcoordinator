const node = (tag, text) => { const item = document.createElement(tag); if (text != null) item.textContent = text; return item; };
export function inspectBim(container, element, context) {
  container.replaceChildren();
  const input = node('input'); input.type = 'search'; input.placeholder = 'Buscar propiedad, valor o identificador';
  input.setAttribute('aria-label', input.placeholder); container.append(input);
  const sections = [];
  const section = (title, rows, open = false) => {
    const details = node('details'); details.open = open;
    details.append(node('summary', title));
    const list = node('dl'); details.append(list); container.append(details);
    const entries = [];
    for (const [label, value] of rows) {
      const term = node('dt', label), description = node('dd', value ?? 'Sin valor');
      list.append(term, description); entries.push({ term, description, text: `${label} ${value}`.toLocaleLowerCase() });
    }
    sections.push({ details, entries });
  };
  const parameters = values => (values ?? []).map(p => [p.name, p.hasValue
    ? `${p.display ?? p.value ?? ''} · ${p.storage} · ID ${p.id}${p.unitId ? ' · ' + p.unitId : ''}${p.siValue != null ? ' · SI: ' + p.siValue : ''}${p.referenceId ? ' · Ref: ' + p.referenceId : ''}`
    : `Sin valor · ${p.storage} · ID ${p.id}`]);
  const bim = element.bim;
  section('Identidad y relaciones', [['ID Revit', element.elementId], ['UniqueId', element.id], ['Categoría', element.category],
    ['Clase IFC', bim?.ifcClass], ['IfcGUID', bim?.ifcGuid], ['Documento', bim?.documentId], ['Tipo', bim?.typeId],
    ['Nivel', context?.levels?.[bim?.levelId]?.name ?? bim?.levelId], ['Anfitrión', bim?.hostId], ['Fase', context?.phases?.[bim?.phaseId] ?? bim?.phaseId]], true);
  if (!bim) { section('Propiedades anteriores · recaptura para BIM completo', Object.entries(element.properties), true); return; }
  section(`Instancia · ${bim.parameters.length} parámetros`, parameters(bim.parameters), true);
  const type = context?.types?.[bim.typeId];
  if (type) {
    section(`Tipo · ${type.name} · Familia ${type.family}`, parameters(type.parameters));
    if (type.layers?.length) section('Capas del tipo', type.layers.map((layer, i) => [`Capa ${i + 1}`, `${context.materials[layer.materialId]?.name ?? 'Sin material'} · ${layer.thickness} m · ${layer.function}`]));
  }
  const materialIds = new Set(element.meshes.map(m => m.materialId).filter(Boolean));
  for (const id of materialIds) {
    const material = context?.materials?.[id];
    if (material) section(`Material · ${material.name}`, parameters(material.parameters));
  }
  const source = context?.documents?.find(d => d.id === bim.documentId);
  if (source) section('Documento y ubicación', [['Título', source.title], ['Número', source.number], ['Posición de proyecto [m, m, m, rad]', source.projectPosition?.join(', ')], ...parameters(source.parameters)]);
  input.addEventListener('input', () => {
    const query = input.value.toLocaleLowerCase();
    for (const { details, entries } of sections) {
      let matches = 0;
      for (const row of entries) { const match = row.text.includes(query); row.term.hidden = row.description.hidden = !match; if (match) matches++; }
      details.hidden = matches === 0; if (query && matches) details.open = true;
    }
  });
}
