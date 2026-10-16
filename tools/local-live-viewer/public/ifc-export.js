export function setupIfcExport(getSnapshot) {
  const button = document.getElementById('generate-ifc');
  const status = document.getElementById('ifc-status');
  const files = document.getElementById('ifc-files');
  let active = false, seenRevision = null;
  const update = () => {
    button.disabled = active || !getSnapshot()?.revisionId;
    if (!active && !getSnapshot()?.bim) status.textContent = 'Sincroniza de nuevo con Cmcoordinator para recibir los datos BIM.';
    else if (!active && getSnapshot()?.revisionId !== seenRevision)
      status.textContent = `Revisión ${getSnapshot().revisionId.slice(0, 8)} lista para generar IFC.`;
    seenRevision = getSnapshot()?.revisionId;
  };
  button.addEventListener('click', async () => {
    if (active) return;
    active = true; button.disabled = true; files.replaceChildren();
    status.textContent = 'Preparando IFC de la revisión guardada…';
    try {
      const response = await fetch('/api/v2/ifc/exports', { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ revisionId: getSnapshot().revisionId }) });
      let job = await response.json();
      if (!response.ok) throw new Error(job.error ?? 'No se pudo iniciar la generación');
      while (job.state === 'preparing' || job.state === 'running') {
        status.textContent = `Generando y validando IFC · ${job.elements} elementos · revisión ${job.revision.slice(0, 8)}`;
        await new Promise(resolve => setTimeout(resolve, 800));
        const poll = await fetch(`/api/v2/ifc/exports/${job.id}`);
        job = await poll.json(); if (!poll.ok) throw new Error(job.error ?? 'No se pudo consultar la exportación');
      }
      if (job.state !== 'completed') throw new Error(job.error ?? 'Falló la generación IFC');
      status.textContent = `IFC validado · revisión ${job.revision.slice(0, 8)} · ${job.report.elements} elementos · ${job.report.typedProperties} propiedades · ${job.report.fallbackClasses.length} clases proxy. Alcance: vista 3D sincronizada.`;
      for (const [name, text] of [['model.ifc', 'Descargar IFC'], ['report.json', 'Informe de cobertura']]) {
        const link = document.createElement('a'); link.href = `/api/v2/ifc/exports/${job.id}/${name}`; link.textContent = text; link.download = ''; files.append(link);
      }
    } catch (error) { status.textContent = error.message; }
    finally { active = false; update(); }
  });
  update(); return update;
}
