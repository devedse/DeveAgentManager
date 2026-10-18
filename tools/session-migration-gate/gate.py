"""Offline CLI regression: resume actual DAM conversions, preserve history, execute only fresh work.

Uses fake credentials, isolated configuration directories and a loopback model stub. No model API or subscription
is contacted. Requires Python 3, .NET 10, codex, claude and pi. Run: python3 tools/session-migration-gate/gate.py
This verifies CLI loading, outgoing protocol and execution; it does not measure real-model hallucination rates
or establish every hosted provider's acceptance policy. Temporary configurations and transcripts are deleted.
"""
import argparse
import http.server
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading


def content_blocks(request):
    for message in request.get('messages', []):
        content = message.get('content', [])
        if isinstance(content, list):
            yield from (b for b in content if isinstance(b, dict))


class ModelStub(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def reply(self, body, kind='application/json'):
        raw = (body if isinstance(body, str) else json.dumps(body)).encode()
        self.send_response(200)
        self.send_header('content-type', kind)
        self.send_header('content-length', str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self):
        self.reply({'models': []})

    def do_POST(self):
        request = json.loads(self.rfile.read(int(self.headers.get('content-length', 0))))
        if 'count_tokens' in self.path:
            self.reply({'input_tokens': 100})
            return
        self.server.requests.append(request)
        assert len(self.server.requests) < 8, 'Unexpected model loop'
        if 'messages' in self.path:
            blocks = list(content_blocks(request))
            done = any(b.get('type') == 'tool_result' and b.get('tool_use_id') == 'fresh_call' for b in blocks)
            name = 'Bash' if self.server.harness == 'claude' else 'bash'
            block = {'type': 'text', 'text': ''} if done else {'type': 'tool_use', 'id': 'fresh_call', 'name': name, 'input': {}}
            message = {'id': 'msg_gate', 'type': 'message', 'role': 'assistant', 'model': 'claude-sonnet-4-5',
                       'content': [], 'stop_reason': None, 'stop_sequence': None, 'usage': {'input_tokens': 10, 'output_tokens': 5}}
            events = [
                {'type': 'message_start', 'message': message},
                {'type': 'content_block_start', 'index': 0, 'content_block': block},
                {'type': 'content_block_delta', 'index': 0, 'delta': {'type': 'text_delta', 'text': 'MIGRATION-GATE-OK'} if done
                    else {'type': 'input_json_delta', 'partial_json': json.dumps({'command': 'printf fresh > fresh.txt', 'description': 'Create test marker'})}},
                {'type': 'content_block_stop', 'index': 0},
                {'type': 'message_delta', 'delta': {'stop_reason': 'end_turn' if done else 'tool_use', 'stop_sequence': None}, 'usage': {'output_tokens': 5}},
                {'type': 'message_stop'},
            ]
        else:
            inputs = request.get('input', [])
            done = any(i.get('type') == 'function_call_output' and i.get('call_id') == 'fresh_call' for i in inputs)
            name = 'exec_command' if self.server.harness == 'codex' else 'bash'
            namespace = None
            if self.server.harness == 'codex':
                # Codex versions can expose shell tools directly or inside a namespace.
                for tool in request.get('tools', []):
                    if tool.get('type') == 'namespace' and any(t.get('name') == name for t in tool.get('tools', [])):
                        namespace = tool['name']
            args = {'cmd': 'printf fresh > fresh.txt'} if name == 'exec_command' else {'command': 'printf fresh > fresh.txt'}
            item = {'type': 'message', 'role': 'assistant', 'id': 'msg_gate', 'status': 'completed', 'content': [{'type': 'output_text', 'text': 'MIGRATION-GATE-OK', 'annotations': []}]} if done else {
                'type': 'function_call', 'id': 'fc_gate', 'call_id': 'fresh_call', 'name': name, 'arguments': json.dumps(args), 'status': 'completed'}
            if namespace:
                item['namespace'] = namespace
            response = {'id': 'resp_gate_' + str(len(self.server.requests)), 'object': 'response', 'status': 'completed', 'output': [item],
                        'usage': {'input_tokens': 10, 'output_tokens': 5, 'total_tokens': 15, 'input_tokens_details': {'cached_tokens': 0}, 'output_tokens_details': {'reasoning_tokens': 0}}}
            events = [{'type': 'response.created', 'response': {**response, 'status': 'in_progress', 'output': []}},
                      {'type': 'response.output_item.added', 'output_index': 0, 'item': {**item, 'arguments': ''} if not done else item}]
            if not done:
                events.append({'type': 'response.function_call_arguments.delta', 'item_id': 'fc_gate', 'output_index': 0, 'delta': item['arguments']})
            events += [{'type': 'response.output_item.done', 'output_index': 0, 'item': item}, {'type': 'response.completed', 'response': response}]
        self.reply(''.join('event: ' + e['type'] + '\ndata: ' + json.dumps(e) + '\n\n' for e in events), 'text/event-stream')


def run_case(folder, api):
    case = json.loads((folder / 'case.json').read_text())
    work = Path(case['work'])
    # Simulate the historical effect already being present before the switch.
    (work / 'historical.txt').write_text('original')
    (work / 'fresh.txt').unlink(missing_ok=True)
    home = folder / ('home-' + api)
    home.mkdir()
    env = {key: os.environ[key] for key in ('PATH', 'LANG', 'TERM', 'DOTNET_ROOT') if key in os.environ}
    env['HOME'] = str(home)
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), ModelStub)
    server.requests, server.harness = [], case['to']
    threading.Thread(target=server.serve_forever, daemon=True).start()
    url = f'http://127.0.0.1:{server.server_port}'
    prompt = 'Use a real shell tool to create fresh.txt, then report completion.'
    if case['to'] == 'claude':
        config = home / 'claude'
        project = config / 'projects' / ''.join(c if c.isascii() and c.isalnum() else '-' for c in str(work))
        project.mkdir(parents=True)
        native = project / (case['id'] + '.jsonl')
        shutil.copyfile(folder / 'session.jsonl', native)
        env.update(CLAUDE_CONFIG_DIR=str(config), ANTHROPIC_BASE_URL=url, ANTHROPIC_API_KEY='dam-test-only',
                   CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC='1', DISABLE_TELEMETRY='1')
        command = ['claude', '--bare', '--resume', case['id'], '--model', 'claude-sonnet-4-5', '--permission-mode', 'bypassPermissions',
                   '--tools', 'Bash', '--output-format', 'stream-json', '--verbose', '-p', prompt]
    elif case['to'] == 'codex':
        config = home / 'codex'
        session_dir = config / 'sessions/2026/01/01'
        session_dir.mkdir(parents=True)
        native = session_dir / ('rollout-2026-01-01T00-00-00-' + case['id'] + '.jsonl')
        shutil.copyfile(folder / 'session.jsonl', native)
        (config / 'config.toml').write_text('model="gpt-6"\nmodel_provider="migration_gate"\n[model_providers.migration_gate]\n'
            f'name="Migration gate"\nbase_url="{url}/v1"\nwire_api="responses"\nrequires_openai_auth=false\n'
            '[analytics]\nenabled=false\n[features]\nresponses_websockets=false\n')
        env['CODEX_HOME'] = str(config)
        command = ['codex', 'exec', '--dangerously-bypass-approvals-and-sandbox', '--skip-git-repo-check', '--json', 'resume', case['id'], prompt]
    else:
        config = home / 'pi'
        config.mkdir()
        (config / 'models.json').write_text(json.dumps({'providers': {'migration-gate': {'baseUrl': url + '/v1', 'api': api, 'apiKey': 'dam-test-only',
            'models': [{'id': 'migration-test', 'reasoning': False, 'input': ['text'], 'contextWindow': 200000, 'maxTokens': 4096}]}}}))
        env.update(PI_CODING_AGENT_DIR=str(config), PI_OFFLINE='1')
        native = home / 'session.jsonl'
        shutil.copyfile(folder / 'session.jsonl', native)
        command = ['pi', '--offline', '--no-extensions', '--no-mcp', '--no-skills', '--no-context-files', '--no-prompt-templates', '--no-themes',
                   '--session', str(native), '--model', 'migration-gate/migration-test', '--tools', 'bash', '--mode', 'json', '-p', prompt]
    try:
        result = subprocess.run(command, cwd=work, env=env, capture_output=True, text=True, timeout=55)
        assert result.returncode == 0, (result.stdout + result.stderr)[-2000:]
        assert (work / 'fresh.txt').read_text() == 'fresh', (result.stdout + result.stderr)[-2000:]
        assert (work / 'historical.txt').read_text() == 'original', 'Historical command was replayed'
        assert not (work / 'never.txt').exists(), 'Opaque historical script was executed'
        assert len(server.requests) >= 2, 'Fresh tool result did not return to the model'
        first = server.requests[0]
        if 'messages' in first:
            blocks = list(content_blocks(first))
            historical = [b for b in blocks if b.get('type') == 'tool_use' and b.get('name') == case['expectedName']]
            assert len(historical) == 1, 'Historical call was flattened or removed'
            assert any(b.get('type') == 'tool_result' and b.get('tool_use_id') == historical[0]['id'] for b in blocks)
            assert any(b.get('type') == 'tool_use' and b.get('id') == 'fresh_call' for b in content_blocks(server.requests[-1]))
        else:
            historical = [i for i in first.get('input', []) if i.get('type') == 'function_call' and i.get('name') == case['expectedName']]
            assert len(historical) == 1, 'Historical call was flattened or removed'
            assert any(i.get('type') == 'function_call_output' and i.get('call_id') == historical[0]['call_id'] for i in first['input'])
            assert any(i.get('type') == 'function_call_output' and i.get('call_id') == 'fresh_call' for i in server.requests[-1].get('input', []))
        # The real harness must also persist the fresh native tool event; text claims cannot satisfy this check.
        lines = [json.loads(line) for line in native.read_text().splitlines() if line.strip()]
        if case['to'] == 'claude':
            recorded = [b for line in lines for b in line.get('message', {}).get('content', []) if isinstance(b, dict) and b.get('type') == 'tool_use' and b.get('id') == 'fresh_call']
        elif case['to'] == 'codex':
            recorded = [line for line in lines if line.get('type') == 'response_item' and line.get('payload', {}).get('type') == 'function_call' and line['payload'].get('call_id') == 'fresh_call']
        else:
            recorded = [b for line in lines for b in line.get('message', {}).get('content', []) if isinstance(b, dict) and b.get('type') == 'toolCall' and b.get('id', '').startswith('fresh_call')]
        assert recorded, 'Fresh execution has no native tool-call record'
        print(f"PASS {case['from']} -> {case['to']} ({api}): structured history, fresh execution, no replay", flush=True)
    finally:
        server.shutdown()
        server.server_close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default=shutil.which('dotnet') or str(Path.home() / '.dotnet/dotnet'))
    args = parser.parse_args()
    for name in ('codex', 'claude', 'pi'):
        assert shutil.which(name), f'{name} must be installed'
        print(subprocess.run([name, '--version'], capture_output=True, text=True, check=True).stdout.strip(), flush=True)
    with tempfile.TemporaryDirectory(prefix='dam-migration-gate-') as root:
        project = Path(__file__).with_name('Export.csproj')
        subprocess.run([args.dotnet, 'run', '--project', str(project), '--', root], check=True, capture_output=True, text=True)
        for folder in sorted(Path(root).iterdir()):
            case = json.loads((folder / 'case.json').read_text())
            for api in (('anthropic-messages', 'openai-responses') if case['to'] == 'pi' else ('native',)):
                run_case(folder, api)
    print('GATE PASS: all six conversion routes; pi tested with both Anthropic and OpenAI wire formats.')


if __name__ == '__main__':
    main()
