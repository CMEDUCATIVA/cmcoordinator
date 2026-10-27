"""Tabla resumen de auditoria.json (línea base o salida del convertidor)."""
import json, sys

rows = json.load(open(sys.argv[1], encoding="utf-8"))
for r in rows:
    if "error" in r:
        print(f"{r['nombre']}: {r['error']}"); continue
    e, gb, gs = r["elementos"], r.get("glb_cajas", {}), r.get("glb_guardadas", {})
    print(f"## {r['nombre']} · Revit {r.get('version')} · {r['mb']} MB")
    print(f"   elementos: salida {e['salida']} · {e['total']} en {e['s']} s / {e['mb_ram']} MB RAM · con tipo {e.get('con_tipo')} · con nivel {e.get('con_nivel')} · parámetros {e.get('parametros')}")
    if e.get("error"): print("   ERROR:", e["error"][-300:])
    print(f"   glb cajas: salida {gb.get('salida')} · {gb.get('s')} s · {gb.get('mb_archivo')} MB · {gb.get('elementos_con_malla')} elem · {gb.get('triangulos')} tri")
    print(f"   glb guardadas: salida {gs.get('salida')} · {gs.get('s')} s / {gs.get('mb_ram')} MB RAM · {gs.get('mb_archivo')} MB · {gs.get('elementos_con_malla')} elem · {gs.get('triangulos')} tri")
    print(f"   clases: {e.get('por_clase')}")
    c = r.get("comparacion")
    if c:
        g = c["geometria"]
        print(f"   ORÁCULO: emparejados {c['emparejados']}/{c['vivo']} · solo vivo {c['solo_vivo']}")
        print(f"     tipo {c['tipo']['coincide']}/{c['tipo']['presente']} · nivel {c['nivel']['coincide']}/{c['nivel']['presente']} · clase IFC {c['clase_ifc']['coincide']}/{c['clase_ifc']['comparables']} · parámetros mismos ids {c['parametros']['mismos_ids']}/{c['parametros']['vivo']}")
        print(f"     geometría: con malla {g['rvtrs_con_malla']}/{g['vivo_con_malla']} · triángulos vivo {g['tri_vivo']} · cajas {g['tri_cajas']} · guardadas {g['tri_guardadas']}")
