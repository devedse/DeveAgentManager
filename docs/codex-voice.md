# Codex voice

Open a running Codex agent and choose **Start voice**, or press **F8**. Allow microphone access when the browser asks.
Use **Mute microphone** to pause input and **End voice** to finish the call. Ending voice leaves the agent running.
Leaving the conversation page or hiding the tab ends the call and releases the microphone. A blocked audio player
offers **Play audio**. Only one browser tab can own voice for an agent at a time.

Use HTTPS (localhost also works), a browser with microphone and WebRTC support, and a Codex login with voice access.
Codex 0.165.0 was verified. After upgrading the Manager, update Codex on the Runner and restart an existing agent once
to enable the connection. Running agents are not restarted automatically. Older CLIs retain the existing terminal
launch when they lack the control-socket proxy; trying voice then explains that an update and restart are needed.

## Connection and process ownership

Each supported Codex agent owns one app-server and one terminal UI, with the terminal attached through `--remote` to
that server. Both use the agent's existing runtime, login, instructions, permissions, working folder and native
conversation. No second conversation or coding process is created when voice starts.

The supervisor runs in the agent's tmux pane. It holds an exclusive per-agent lock, creates a short private Unix socket
under `/tmp`, and records its socket and native conversation in `~/.deveagentmanager/codex-voice/<agent>/connection.json`.
Codex's server log is alongside that record. Normal exit and termination stop both children and clean up the socket.
Linux parent-death signals also terminate children when the supervisor is killed. Long account runtime paths therefore
do not exceed the Unix socket path limit. Permissions and developer instructions are configured on the server because
Codex rejects permission overrides on a remote terminal resume.

The Portal opens an authenticated voice WebSocket. The Manager checks Write access, login enforcement and the active
agent identity, then connects to its existing socket through a dedicated SSH channel. It forwards only initialization,
voice start with a bounded SDP offer, the SDP answer, and voice stop. The browser never receives Codex credentials or
general app-server RPC access. Negotiation and SSH run without the session lock. Stop, restart, conversation changes
and Runner disconnects end the voice connection. Access revocation uses the same connection enforcement as terminals.

Microphone audio and replies use the browser's WebRTC connection to the voice service. The Manager does not record that
audio. It preserves Codex's native history as usual, including `realtime_item` records; transcript segments appear as
user and assistant messages. Signaling logs contain neither SDP nor provider payloads. The Portal permits microphone
access for its own origin only. Voice starts explicitly and does not reconnect or replay requests automatically.

## Verification

`CodexVoiceTests` checks transcript reading, incremental parity and exact native round trips, plus bidirectional
WebSocket traffic over a disposable real SSH server and bounded message handling. `HarnessScriptTests` checks launch
settings and child cleanup after graceful termination and a killed supervisor. `ApiSecurityTests` checks login and
origin enforcement. `Browser/codex-voice.mts` checks real browser microphone-track cleanup with controlled signaling,
including denied permission, mute, blocked playback, competing tabs, cancellation while permission is pending,
navigation, hidden tabs, read-only access and narrow light/dark layouts.

Live verification with Codex 0.165.0 used synthesized speech as a Chromium microphone input, the Portal's real
authenticated endpoint, a disposable SSH Runner and a real WebRTC connection. A spoken request created a file in the
existing conversation, replies returned audio, and a typed request executed in the same terminal after ending voice.
The call stayed connected beyond its opening deadline; mute, microphone release and rejection of a competing tab were
also checked. Physical microphone/speaker behavior and mobile audio remain separate checks.

All tmux fixtures pin a unique socket with `-S`, including cleanup and application commands reached through their PATH
wrapper. `TmuxIsolationTests` contaminates `TMUX` with a second disposable server and checks that it survives fixture
cleanup. See the test process isolation instructions in `AGENTS.md`.
