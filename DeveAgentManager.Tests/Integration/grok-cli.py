"""Official CLI smoke test against a local model; no live account or paid API requests.

Invoked by GrokCliTests when DAM_GROK_CLI points to the installed official CLI.
"""
import http.server
import json
import os
import pathlib
import shlex
import subprocess
import sys
import threading
import time

binary, root_arg, session_id, mode = sys.argv[1:]
root = pathlib.Path(root_arg)
home = root / 'grok'
workspace = root / 'workspace'
workspace.mkdir(exist_ok=True)
requests = []


class Model(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_GET(self):
        self.send_response(200)
        self.end_headers()
        self.wfile.write(json.dumps({'data': [{'id': 'dam-test', 'object': 'model', 'context_window': 128000}]}).encode())

    def do_POST(self):
        request = json.loads(self.rfile.read(int(self.headers.get('Content-Length', 0))))
        if 'tools' in request:
            requests.append(request)
            if os.environ.get('DAM_GROK_TUI') == '1':
                time.sleep(2)
        self.send_response(200)
        self.send_header('Content-Type', 'text/event-stream')
        self.end_headers()
        chunks = [({'role': 'assistant', 'content': 'Verified test reply.'}, None), ({}, 'stop')]
        try:
            for delta, finish in chunks:
                event = {'id': 'test-only', 'created': 1792368000, 'model': 'dam-test', 'object': 'chat.completion.chunk',
                         'choices': [{'index': 0, 'delta': delta, 'finish_reason': finish}]}
                if finish:
                    event['usage'] = {'prompt_tokens': 10, 'completion_tokens': 7, 'total_tokens': 17}
                self.wfile.write(('data: ' + json.dumps(event) + '\n\n').encode())
            self.wfile.write(b'data: [DONE]\n\n')
        except (BrokenPipeError, ConnectionResetError):
            pass


server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Model)
threading.Thread(target=server.serve_forever, daemon=True).start()
url = f'http://127.0.0.1:{server.server_port}/v1'
(home / 'config.toml').write_text(f'''[model.dam-test]
model = "dam-test"
api_key = "xai-test-only"
base_url = "{url}"
[features]
telemetry = false
[cli]
auto_update = false
''')
env = {**os.environ, 'HOME': str(root), 'GROK_HOME': str(home), 'GROK_DISABLE_AUTOUPDATER': '1',
       'GROK_AGENT_DASHBOARD': '0', 'GROK_MODELS_BASE_URL': url, 'XAI_API_KEY': 'xai-test-only'}
for name in ('GROK_AUTH', 'GROK_AUTH_PATH', 'GROK_CONFIG', 'GROK_CONFIG_PATH'):
    env.pop(name, None)
overlay = root / 'grok-config.json'
overlay.write_text(json.dumps({'models': {'default': 'dam-test'}}))
env['GROK_CONFIG_PATH'] = str(overlay)
flags = [binary, '--leader-socket', str(root / 'leader.sock'), '--trust', '--always-approve',
         '--no-subagents', '--disable-web-search']


def run(args):
    result = subprocess.run(args, cwd=workspace, env=env, capture_output=True, text=True, timeout=45)
    if result.returncode:
        raise AssertionError(result.stderr[-2000:] + '\n' + result.stdout[-1000:])
    return result.stdout


try:
    if mode == 'restored':
        run(flags + ['--resume', session_id, '-p', 'Continue after restoring the backup', '--output-format', 'streaming-json'])
        assert 'Compacted context test marker' in json.dumps(requests[-1]), 'Restored compacted context was lost'
        print('Official CLI resumed a Manager-exported bundle with its saved replay checkpoint.', flush=True)
        sys.exit(0)
    run(flags + ['--resume', session_id, '-p', 'Continue the imported conversation', '--output-format', 'streaming-json'])
    assert requests, 'No model request reached the local server'
    history = json.dumps(requests[-1])
    assert 'First message' in history and 'test-only result' in history, 'Imported history was lost on resume'
    assert any(m.get('tool_calls') for m in requests[-1]['messages']), 'Historical tool calls were not replayed'
    assert any(m.get('role') == 'tool' for m in requests[-1]['messages']), 'Historical tool results were not replayed'
    print('Official CLI restored the Manager-written history and tool protocol.', flush=True)

    if os.environ.get('DAM_GROK_TUI') == '1':
        socket = 'dam-grok-test-' + str(os.getpid())
        tmux = ['tmux', '-L', socket]
        try:
            subprocess.run(tmux + ['new-session', '-d', '-x', '120', '-y', '40',
                                  shlex.join(flags + ['--fullscreen', '--resume', session_id])], env=env, cwd=workspace, check=True)
            time.sleep(3)
            subprocess.run(tmux + ['send-keys', '-l', 'Test the composer'], check=True)
            time.sleep(0.2)
            ready = subprocess.check_output(tmux + ['capture-pane', '-p'], text=True)
            (root / 'ready-screen.txt').write_text(ready)
            subprocess.run(tmux + ['send-keys', 'Enter'], check=True)
            time.sleep(0.8)
            working = subprocess.check_output(tmux + ['capture-pane', '-p'], text=True)
            (root / 'working-screen.txt').write_text(working)
            time.sleep(3)
            destination = os.environ.get('DAM_GROK_CAPTURE')
            if destination:
                pathlib.Path(destination).mkdir(exist_ok=True)
                (pathlib.Path(destination) / 'ready.txt').write_text(ready.replace(str(root), '/workspace'))
                (pathlib.Path(destination) / 'working.txt').write_text(working.replace(str(root), '/workspace'))
        finally:
            subprocess.run(tmux + ['kill-server'], capture_output=True)

    # Force a cold reconstruction from the authoritative log and its compaction checkpoint.
    folder = next((home / 'sessions').glob('*/' + session_id))
    checkpoint_id = '0199f000-0000-7000-8000-000000000099'
    checkpoint_name = 'compaction_checkpoints/' + checkpoint_id + '.json'
    (folder / 'compaction_checkpoints').mkdir(exist_ok=True)
    checkpoint = {'checkpoint_id': checkpoint_id, 'prompt_index_at_compaction': 1, 'schema_version': 1,
                  'created_at': '2026-10-19T00:00:00Z', 'compacted_history': [
                      {'type': 'user', 'content': [{'type': 'text', 'text': 'Compacted context test marker'}]}]}
    (folder / checkpoint_name).write_text(json.dumps(checkpoint))
    with (folder / 'updates.jsonl').open('a') as output:
        output.write(json.dumps({'timestamp': 1792368010, 'method': '_x.ai/session/update', 'params': {
            'sessionId': session_id, 'update': {'sessionUpdate': 'compaction_checkpoint',
                'checkpoint_id': checkpoint_id, 'checkpoint_file': checkpoint_name, 'prompt_index_at_compaction': 1,
                'schema_version': 1, 'created_at': '2026-10-19T00:00:00Z'}}}) + '\n')
    # This is the cache Grok writes on compaction. Its current cold-rebuild path discards the checkpoint's base,
    # so a Manager backup must preserve this file alongside the authoritative updates and checkpoint.
    (folder / 'chat_history.jsonl').write_text(''.join(json.dumps(item) + '\n' for item in checkpoint['compacted_history']))
    run(flags + ['--resume', session_id, '-p', 'Continue after compaction', '--output-format', 'streaming-json'])
    assert 'Compacted context test marker' in json.dumps(requests[-1]), 'Compacted context was lost on resume'
    print('Official CLI retained compacted context with its saved replay checkpoint and cache.', flush=True)
finally:
    server.shutdown()
