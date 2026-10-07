"""
Checks the installed Codex CLI against what DeveAgentManager's central login renewal relies on (AccountService,
CodexHarness.RunnerAccountCopy): with fake tokens and a local HTTPS mock of ChatGPT, a Runner's access-token-only
auth.json works, a 401 makes Codex reload the file the Manager pushed, and an agent still holding a refresh token adopts
the pushed file instead of renewing. Any request to the token endpoint fails the gate. Run after upgrading Codex:

    python3 tools/codex-auth-gate/gate.py      (needs codex and openssl on PATH)
"""
import base64, json, os, pathlib, subprocess, tempfile, threading, time, http.server, queue, ssl

def jwt(exp, tag):
    b64 = lambda v: base64.urlsafe_b64encode(json.dumps(v).encode()).decode().rstrip('=')
    claims = {'sub': 'fake-user', 'email': 'fake@example.invalid', 'exp': exp, 'tag': tag,
              'https://api.openai.com/auth': {'chatgpt_account_id': 'fake-account', 'chatgpt_user_id': 'fake-user', 'chatgpt_plan_type': 'pro'}}
    return b64({'alg': 'none', 'typ': 'JWT'}) + '.' + b64(claims) + '.sig'

def central_file(access, exp=None):  # what DAM puts on a Runner: no refresh token, never refreshed by Codex
    return json.dumps({'auth_mode': 'chatgptAuthTokens', 'OPENAI_API_KEY': None,
                       'tokens': {'id_token': jwt(exp or int(time.time()) + 3600, 'id'), 'access_token': access, 'refresh_token': '', 'account_id': 'fake-account'},
                       'last_refresh': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())})

class Backend(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def reply(self, status, body, kind='application/json'):
        raw = body.encode() if isinstance(body, str) else json.dumps(body).encode()
        self.send_response(status); self.send_header('Content-Type', kind); self.send_header('Content-Length', str(len(raw))); self.end_headers(); self.wfile.write(raw)
    def do_GET(self):
        if self.path.endswith('/accounts/check'):
            self.reply(200, {'accounts': [{'id': 'fake-account', 'plan_type': 'pro', 'workspace_backend_origin': 'NO_CONSTRAINT', 'account_routing_override': 'NO_CONSTRAINT'}], 'account_ordering': ['fake-account'], 'default_account_id': 'fake-account'}); return
        self.reply(200, {'models': []})
    def do_POST(self):
        self.rfile.read(int(self.headers.get('Content-Length', 0)))
        s = self.server
        if self.path.endswith('/oauth/token'):
            s.refreshes += 1; self.reply(500, {'error': 'gate: refresh must never be called'}); return
        if not self.path.endswith('/responses'): self.reply(404, {}); return
        token = (self.headers.get('Authorization') or '').removeprefix('Bearer ')
        s.seen.append(token)
        if token != s.valid:
            if s.on_401: s.on_401(); s.on_401 = None  # DAM pushes the renewed file while the agent is mid-request
            self.reply(401, {'error': {'message': 'expired', 'code': 'token_expired'}}); return
        ev = [{'type': 'response.created', 'response': {'id': 'r1'}},
              {'type': 'response.output_item.done', 'item': {'type': 'message', 'role': 'assistant', 'id': 'm1', 'content': [{'type': 'output_text', 'text': 'GATE-OK'}]}},
              {'type': 'response.completed', 'response': {'id': 'r1', 'usage': {'input_tokens': 0, 'input_tokens_details': None, 'output_tokens': 0, 'output_tokens_details': None, 'total_tokens': 0}}}]
        self.reply(200, ''.join(f"event: {e['type']}\ndata: {json.dumps(e)}\n\n" for e in ev), 'text/event-stream')

server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Backend)
server.refreshes, server.seen, server.on_401 = 0, [], None
CERTS = pathlib.Path(tempfile.mkdtemp(prefix='codex-gate-certs-'))
for command in (
    'openssl req -x509 -newkey rsa:2048 -nodes -keyout ca.key -out ca.pem -days 1 -subj /CN=gate-ca',
    'openssl req -newkey rsa:2048 -nodes -keyout key.pem -out leaf.csr -subj /CN=localhost',
    'openssl x509 -req -in leaf.csr -CA ca.pem -CAkey ca.key -CAcreateserial -out cert.pem -days 1 -extfile leaf.ext'):
    (CERTS / 'leaf.ext').write_text('basicConstraints=CA:FALSE\nsubjectAltName=DNS:localhost\nextendedKeyUsage=serverAuth\n')  # rustls refuses a CA as server cert
    subprocess.run(command.split(), cwd=CERTS, check=True, capture_output=True)
ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); ctx.load_cert_chain(str(CERTS / 'cert.pem'), str(CERTS / 'key.pem'))
server.socket = ctx.wrap_socket(server.socket, server_side=True)
threading.Thread(target=server.serve_forever, daemon=True).start()
port = server.server_port

def home(root, name, auth):
    h = pathlib.Path(root) / name; h.mkdir()
    (h / 'auth.json').write_text(auth)
    (h / 'config.toml').write_text(f'cli_auth_credentials_store = "file"\nopenai_base_url = "https://localhost:{port}/backend-api/codex"\n'
                                   f'chatgpt_base_url = "https://localhost:{port}/backend-api"\n[analytics]\nenabled = false\n[features]\nresponses_websockets = false\n')
    return h

def env(h): return {'PATH': os.environ['PATH'], 'HOME': str(h), 'CODEX_HOME': str(h), 'RUST_LOG': 'error', 'CODEX_CA_CERTIFICATE': str(CERTS / 'ca.pem'),
                    'CODEX_REFRESH_TOKEN_URL_OVERRIDE': f'https://localhost:{port}/oauth/token'}

def run_exec(h):
    r = subprocess.run(['codex', 'exec', '--skip-git-repo-check', '--ephemeral', 'say hi'], cwd=h, env=env(h), capture_output=True, text=True, timeout=90)
    return r.returncode, r.stdout + r.stderr

with tempfile.TemporaryDirectory(prefix='codex-gate-') as root:
    t1, t2 = jwt(int(time.time()) + 3600, 't1'), jwt(int(time.time()) + 7200, 't2')

    # 1. Runner copy: request uses the access token; nothing refreshes.
    h = home(root, 'a', central_file(t1)); server.valid = t1
    code, out = run_exec(h)
    assert code == 0 and 'GATE-OK' in out, out[-1500:]
    print('1 access-token-only file works: refresh calls =', server.refreshes)

    # 2. Expired on the Runner, DAM pushes a new file during the request: Codex reloads it, retries, no refresh call.
    h = home(root, 'b', central_file(t1)); server.valid = t2
    server.on_401 = lambda: (h / 'auth.json').write_text(central_file(t2))
    code, out = run_exec(h)
    assert code == 0 and 'GATE-OK' in out, out[-1500:]
    print('2 401 -> reload pushed file -> retry OK: retried with the pushed token =', server.seen[-1] == t2, 'refresh calls =', server.refreshes)

    # 3. Expired token and DAM can't renew: fails cleanly, still no refresh call (no rogue authority).
    h = home(root, 'c', central_file(jwt(int(time.time()) - 60, 'old'), int(time.time()) - 60)); server.valid = t2
    code, out = run_exec(h)
    print('3 expired, nothing pushed: exit =', code, 'refresh calls =', server.refreshes)

    WINDOW = 5 * 60
    # 4. Legacy agent already running with a refresh token in memory; DAM replaces its file. Near expiry it must
    #    adopt the file (guarded reload) instead of refreshing.
    legacy = json.dumps({'auth_mode': 'chatgpt', 'OPENAI_API_KEY': None,
                         'tokens': {'id_token': jwt(int(time.time()) + WINDOW + 25, 'id'), 'access_token': jwt(int(time.time()) + WINDOW + 25, 'near'), 'refresh_token': 'fake-legacy', 'account_id': 'fake-account'},
                         'last_refresh': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())})
    h = home(root, 'd', legacy)
    p = subprocess.Popen(['codex', 'app-server'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, env=env(h), cwd=h)
    q = queue.Queue(); threading.Thread(target=lambda: [q.put(l) for l in p.stdout], daemon=True).start()
    def call(i, method, params):
        p.stdin.write(json.dumps({'id': i, 'method': method, 'params': params}) + '\n'); p.stdin.flush()
        end = time.monotonic() + 30
        while time.monotonic() < end:
            try: v = json.loads(q.get(timeout=1))
            except (queue.Empty, ValueError): continue
            if v.get('id') == i: return v
        raise TimeoutError(method)
    try:
        call(1, 'initialize', {'clientInfo': {'name': 'gate', 'version': '1'}}); p.stdin.write('{"method":"initialized"}\n'); p.stdin.flush()
        before = server.refreshes
        (h / 'auth.json').write_text(central_file(t2))
        time.sleep(35)  # now inside the legacy token's refresh window
        r = call(2, 'getAuthStatus', {'includeToken': True, 'refreshToken': False})
        token = r.get('result', {}).get('authToken')
        print('4 legacy process adopts pushed file: token is new =', token == t2, 'refresh calls =', server.refreshes - before)
        assert token == t2, r
    finally:
        p.terminate(); p.wait(timeout=10)

assert server.refreshes == 0, f'{server.refreshes} refresh calls'
print('GATE PASS: Codex', subprocess.run(['codex', '--version'], capture_output=True, text=True).stdout.strip(), '- no refresh request in any scenario')
server.shutdown()
