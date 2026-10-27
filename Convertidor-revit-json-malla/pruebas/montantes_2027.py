"""Compara los campos de nivel de rvt-rs de los montantes de muro cortina entre las muestras 2026 y 2027 de Autodesk."""
import collections, json, os, subprocess

B = os.path.expanduser("~/rvtrs/rvt-rs-v0.7.0-x86_64-unknown-linux-musl")
D = os.path.expanduser("~/corpus_autodesk")
ora = {o["elementId"]: o for o in json.load(open("/mnt/d/Cmcoordinator/Convertidor-revit-json-malla/pruebas/oraculos/canal_8.json", encoding="utf-8"))}
mull = [i for i, o in ora.items() if o["category"] == "Montantes de muro cortina"]
for year in ("2026", "2027"):
    els = {str(e["id"]): e for e in json.loads(subprocess.run([B + "/rvt-elements", f"{D}/{year}-racbasicsampleproject.rvt"], capture_output=True, text=True).stdout) if e.get("id") is not None}
    levels = {str(e["id"]): next((f.get("value") for f in e["fields"] if f["name"] in ("m_name", "m_level_name")), "?") for e in els.values() if e["class_name"] == "Level"}
    c = collections.Counter()
    for i in mull:
        if i not in els: continue
        f = {x["name"]: x.get("value", x.get("id")) for x in els[i]["fields"]}
        c[(ora[i]["levelName"], str(f.get("m_levelId")), str(f.get("m_level_parameter")), str(f.get("m_aggregate_whole") is not None))] += 1
    print(year, "montantes", len(mull))
    for k, v in c.most_common(8): print("   oráculo/m_levelId/m_level_parameter/con agregado:", k, v)
