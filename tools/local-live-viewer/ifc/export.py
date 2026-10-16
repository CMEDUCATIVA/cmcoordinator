import json
import sys
from pathlib import Path
from writer import write


if __name__ == '__main__':
    try:
        snapshot = json.loads(Path(sys.argv[1]).read_text(encoding='utf-8'))
        report = write(snapshot, sys.argv[2])
        Path(sys.argv[3]).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        print(json.dumps({'ok': True, 'elements': report['elements']}))
    except Exception as error:
        print(json.dumps({'ok': False, 'error': str(error)}, ensure_ascii=True), file=sys.stderr)
        sys.exit(1)
