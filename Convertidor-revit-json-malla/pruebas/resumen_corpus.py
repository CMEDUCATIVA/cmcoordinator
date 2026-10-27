"""Resume la última ejecución de corpus.sh: resumen.json y auditoria.json de cada caso.
Uso (WSL): resumen_corpus.py [mixtas] [prefijo]  (prefijo «ad-» o «gh-»: todas las carpetas de corpus_autodesk.sh o corpus_github.sh)"""
import glob, json, os, sys

H = os.path.expanduser("~/cmconv")
MALLAS = sys.argv[1] if len(sys.argv) > 1 else "mixtas"
CASOS = ["casa-2023", "arquitectura-2024", "estructura-2024", "mep-2024", "vivienda-est-2025",
         "arquitectura-2026", "casa-2026", "rstadvanced-2026", "arquitectura-2027"]
if len(sys.argv) > 2:
    suffix = f"-{MALLAS}"
    CASOS = sorted(os.path.basename(p)[:-len(suffix)] for p in glob.glob(os.path.join(H, sys.argv[2] + "*" + suffix)))
for n in CASOS:
    d = os.path.join(H, f"{n}-{MALLAS}")
    if not os.path.exists(d + "/resumen.json"):
        print(f"{n:18} sin conversión"); continue
    r = json.load(open(d + "/resumen.json", encoding="utf-8"))
    print(f"{n:18} código={r['codigo']} elementos={r.get('elementos_convertidos')} con_malla={r.get('con_malla')} triángulos={r.get('triangulos')} "
          f"MB={r.get('mb')} s={r.get('segundos', {}).get('total')} sin_malla={r.get('sin_malla')} {r.get('error', '')}")
    a_path = d + "/auditoria.json"
    if not os.path.exists(a_path) or os.path.getmtime(a_path) < os.path.getmtime(d + "/resumen.json"):
        continue
    a = json.load(open(a_path, encoding="utf-8"))
    print("    ", {k: a[k] for k in ("emparejados", "categoria", "clase_ifc", "tipo_predefinido", "nivel", "tipo", "familia", "ifc_guid")})
    print("     agregados", a["agregados"]["mismo_padre"], "· psets", a["conjuntos_ifc"]["propiedades_presentes"], "iguales", a["conjuntos_ifc"]["valores_iguales"],
          "· parámetros con nombre", a["parametros"]["con_nombre"], "· geometría", a["geometria"])
    print("     dif_clase", a["diferencias_clase"][:3], "· dif_nivel", a.get("diferencias_nivel", [])[:3])
    print("     solo_oráculo", a["solo_oraculo"], "· solo_convertidor", a["solo_convertidor"])
