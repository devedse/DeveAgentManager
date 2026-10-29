"""Private signaling between the authenticated browser and Codex's native audio-helper adapter.

Audio stays on browser WebRTC. This SSH stdio WebSocket carries bounded SDP, device
readiness, mute/speaker controls and measured levels. The terminal owns the call.
"""
import base64
import fcntl
import hashlib
import json
import os
from pathlib import Path
import select
import signal
import socket
import struct
import subprocess
import sys

os.umask(0o077)
agent, session = sys.argv[1:]
stopping = False
owner = listener = helper = None
address = None


class VoiceError(Exception):
    pass


def stop(*_):
    global stopping
    stopping = True


def send_frame(opcode, data):
    length = len(data)
    if length < 126:
        header = bytes([0x80 | opcode, length])
    elif length <= 65535:
        header = bytes([0x80 | opcode, 126]) + struct.pack('!H', length)
    else:
        header = bytes([0x80 | opcode, 127]) + struct.pack('!Q', length)
    sys.stdout.buffer.write(header + data)
    sys.stdout.buffer.flush()


def browser(value):
    send_frame(1, json.dumps(value).encode())


def native(value):
    helper.sendall(json.dumps(value).encode() + b'\n')


for sig in (signal.SIGTERM, signal.SIGHUP, signal.SIGINT):
    signal.signal(sig, stop)

headers = {}
size = 0
while True:
    line = sys.stdin.buffer.readline(8193)
    size += len(line)
    if size > 8192 or not line:
        sys.exit(1)
    if line == b'\r\n':
        break
    if b':' in line:
        key, value = line.split(b':', 1)
        headers[key.strip().lower()] = value.strip()
key = headers.get(b'sec-websocket-key')
if not key or len(key) > 128:
    sys.exit(1)
accept = base64.b64encode(hashlib.sha1(key + b'258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest())
sys.stdout.buffer.write(b'HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ' + accept + b'\r\n\r\n')
sys.stdout.buffer.flush()

try:
    state_dir = Path.home() / '.deveagentmanager' / 'codex-voice' / agent
    state = json.loads((state_dir / 'connection.json').read_text())
    if state['sessionId'] != session:
        raise VoiceError('The conversation changed. Connect voice again from its current conversation.')
    os.kill(state['supervisor'], 0)
    if not state.get('nativeAdapter') or not state.get('tmuxSocket') or not state.get('tmuxPane'):
        raise VoiceError('Update Codex and restart this agent once after updating the Manager to prepare its terminal audio adapter.')
    owner = (state_dir / 'native-owner.lock').open('w')
    try:
        fcntl.flock(owner, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        raise VoiceError('The previous terminal audio connection is still stopping. Try again shortly.')
    address = Path(state['nativeAudioSocket'])
    address.unlink(missing_ok=True)
    listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    listener.bind(str(address))
    listener.listen(1)
    listener.setblocking(False)
    browser(dict(type='ready'))
    incoming = bytearray()
    helper_buffer = bytearray()
    offer = None
    awaiting = None
    active = False
    microphone_peak = speaker_peak = 0
    while not stopping:
        sources = [0, listener] + ([helper] if helper is not None else [])
        ready, _, _ = select.select(sources, [], [], .2)
        if listener in ready:
            accepted, _ = listener.accept()
            if helper is not None:
                accepted.close()
            else:
                helper = accepted
                helper.settimeout(2)
        if helper is not None and helper in ready:
            data = helper.recv(16384)
            if not data:
                browser(dict(type='closed'))
                break
            helper_buffer.extend(data)
            if len(helper_buffer) > 128 * 1024:
                raise VoiceError('Invalid native audio control.')
            while b'\n' in helper_buffer:
                line, _, helper_buffer = helper_buffer.partition(b'\n')
                message = json.loads(line)
                kind = message.get('type')
                if kind == 'offer' and offer is not None:
                    native(dict(type='offer', sdp=offer))
                elif kind == 'answer' and isinstance(message.get('sdp'), str) and 0 < len(message['sdp']) <= 65536:
                    browser(dict(type='answer', sdp=message['sdp']))
                    awaiting = 'transportReady'
                elif kind == 'devices':
                    browser(dict(type='devices'))
                    awaiting = 'devicesOpened'
                elif kind == 'controls' and isinstance(message.get('microphoneMuted'), bool) and isinstance(message.get('speakerSuppressed'), bool):
                    browser(dict(type='controls', microphoneMuted=message['microphoneMuted'], speakerSuppressed=message['speakerSuppressed']))
                    awaiting = 'controlsApplied'
                elif kind == 'levels':
                    native(dict(type='levels', microphonePeak=microphone_peak, speakerPeak=speaker_peak))
                    microphone_peak = speaker_peak = 0
                elif kind == 'close':
                    native(dict(type='closed'))
                    browser(dict(type='closed'))
                    stopping = True
                else:
                    raise VoiceError('Unexpected native audio control.')
        if 0 not in ready:
            continue
        data = os.read(0, 16384)
        if not data:
            break
        incoming.extend(data)
        if len(incoming) > 256 * 1024:
            raise VoiceError('Invalid terminal voice message.')
        while len(incoming) >= 2:
            first, second = incoming[:2]
            opcode, length, offset = first & 15, second & 127, 2
            if not first & 128 or not second & 128 or opcode not in (1, 8, 9, 10):
                raise VoiceError('Invalid terminal voice message.')
            if length in (126, 127):
                count = 2 if length == 126 else 8
                if len(incoming) < 2 + count:
                    break
                length = int.from_bytes(incoming[2:2 + count], 'big')
                offset += count
            if length > 70 * 1024 or (opcode >= 8 and length > 125):
                raise VoiceError('Terminal voice message is too large.')
            if len(incoming) < offset + 4 + length:
                break
            mask = incoming[offset:offset + 4]
            payload = bytes(value ^ mask[index % 4] for index, value in enumerate(incoming[offset + 4:offset + 4 + length]))
            del incoming[:offset + 4 + length]
            if opcode == 8:
                stopping = True
                break
            if opcode == 9:
                send_frame(10, payload)
                continue
            if opcode != 1:
                continue
            message = json.loads(payload)
            kind = message.get('type')
            if kind == 'start' and offer is None and isinstance(message.get('sdp'), str) and 0 < len(message['sdp']) <= 65536:
                offer = message['sdp']
                subprocess.run(['tmux', '-S', state['tmuxSocket'], 'send-keys', '-t', state['tmuxPane'], 'F8'], check=True,
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=5)
            elif kind == 'connected' and awaiting == 'transportReady':
                native(dict(type='transportReady'))
                awaiting = None
            elif kind == 'devicesOpened' and awaiting == 'devicesOpened' and message.get('available') is True:
                native(dict(type='devicesOpened'))
                awaiting = None
            elif kind == 'controlsApplied' and awaiting == 'controlsApplied':
                native(dict(type='controlsApplied'))
                awaiting = None
                if not active:
                    browser(dict(type='active'))
                    active = True
            elif kind == 'levels' and all(isinstance(message.get(field), int) and 0 <= message[field] <= 65535 for field in ('microphonePeak', 'speakerPeak')):
                microphone_peak = max(microphone_peak, message['microphonePeak'])
                speaker_peak = max(speaker_peak, message['speakerPeak'])
            elif kind == 'stop':
                stopping = True
            else:
                raise VoiceError('Unexpected terminal voice control.')
except VoiceError as error:
    try:
        browser(dict(type='error', message=str(error)))
    except OSError:
        pass
except Exception:
    try:
        browser(dict(type='error', message='Terminal audio could not connect. Update Codex and restart this agent after updating the Manager.'))
    except OSError:
        pass
finally:
    if helper is not None:
        helper.close()
    if listener is not None:
        listener.close()
    if address is not None:
        address.unlink(missing_ok=True)
    if owner is not None:
        owner.close()
