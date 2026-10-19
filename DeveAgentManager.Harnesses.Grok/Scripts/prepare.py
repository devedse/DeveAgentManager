import json
import os
import pathlib
import sys
import uuid
from datetime import datetime, timezone

session_id = str(uuid.UUID(sys.argv[1]))
title = sys.argv[2]
home = pathlib.Path(os.environ.get('GROK_HOME', str(pathlib.Path.home() / '.grok')))
home.mkdir(parents=True, exist_ok=True, mode=0o700)
found = list((home / 'sessions').glob('*/' + session_id))
if not found:
    print('--session-id')
else:
    folder = found[0]
    # Long encoded paths use Grok's slug/hash bucket and require the cwd recovery marker.
    (folder.parent / '.cwd').write_text(os.getcwd())
    (folder.parent / '.cwd').chmod(0o600)
    summary = folder / 'summary.json'
    # Imported ACP logs have no index file. Native conversations retain their existing metadata.
    if not summary.exists():
        now = datetime.now(timezone.utc).isoformat()
        summary.write_text(json.dumps({
            'info': {'id': session_id, 'cwd': os.getcwd()},
            'session_summary': title, 'created_at': now, 'updated_at': now,
            'num_messages': 0, 'num_chat_messages': 0,
            'current_model_id': '', 'chat_format_version': 1
        }))
        summary.chmod(0o600)
    print('--resume')
