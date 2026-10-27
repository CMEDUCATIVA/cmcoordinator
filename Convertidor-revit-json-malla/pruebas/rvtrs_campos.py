"""Estructura de los elementos que devuelve rvt-elements: campos, propiedades e identificadores."""
import json, sys, collections

data = json.load(open(sys.argv[1], encoding="utf-8"))
elements = data if isinstance(data, list) else data.get("elements", data.get("items", []))
print("TIPO RAIZ", type(data).__name__, "· claves raíz", list(data.keys())[:15] if isinstance(data, dict) else "-")
print("ELEMENTOS", len(elements))
keys = collections.Counter(k for e in elements for k in e.keys())
print("CAMPOS", dict(keys.most_common(40)))
wall = next((e for e in elements if "Wall" == str(e.get("class") or e.get("class_name") or "")), elements[0])
text = json.dumps(wall, ensure_ascii=False)
print("MUESTRA (muro)", text[:3500])
