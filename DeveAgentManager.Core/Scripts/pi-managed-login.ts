// Loaded only for the explicit DAM managed-login preview. No OAuth refresh credentials are stored in pi.
import { readFile, stat } from "node:fs/promises";
import { homedir } from "node:os";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { builtinProviders } from "@earendil-works/pi-ai/providers/all";

export async function readCodexAccessToken(file: string): Promise<string> {
    try {
        if ((await stat(file)).size > 262144) throw new Error();
        const data = JSON.parse(await readFile(file, "utf8"));
        if (data.auth_mode !== "chatgptAuthTokens" || data.tokens?.refresh_token || typeof data.tokens?.access_token !== "string" || !data.tokens.access_token)
            throw new Error();
        return data.tokens.access_token;
    } catch {
        throw new Error("DAM's Codex login is unavailable. Check the selected login in Harness logins; pi cannot refresh or replace it.");
    }
}

export default async function managedLogin(pi: ExtensionAPI): Promise<void> {
    if (process.env.DAM_PI_LOGIN_MANIFEST) {
        await registerLogins(pi, process.env.DAM_PI_LOGIN_MANIFEST);
        return;
    }
    const selected = process.env.DAM_PI_LOGIN_PROVIDER;
    if (selected !== "claude-code" && selected !== "dam-codex") throw new Error("Missing DAM pi login selection.");
    if (selected === "dam-codex") {
        const file = process.env.DAM_PI_CODEX_AUTH_FILE;
        if (!file) throw new Error("Missing DAM Codex login file.");
        const native = builtinProviders().find(provider => provider.id === "openai-codex");
        if (!native) throw new Error("This pi version does not provide the native Codex interface required by the DAM preview.");
        pi.registerProvider({
            ...native,
            id: "dam-codex",
            name: "Codex (DAM login, legacy preview)",
            getModels: () => native.getModels().map(model => ({ ...model, provider: "dam-codex" })),
            getAllModels: () => native.getModels().map(model => ({ ...model, provider: "dam-codex" })),
            auth: {
                apiKey: {
                    name: "DAM saved Codex login",
                    async check() {
                        await readCodexAccessToken(file);
                        return { type: "oauth", source: "DAM saved Codex login" };
                    },
                    async resolve() {
                        return { auth: { apiKey: await readCodexAccessToken(file) }, source: "DAM saved Codex login" };
                    },
                },
            },
        });
    }

    pi.on("session_start", async (_event, ctx) => {
        if (ctx.model?.provider === selected) return;
        const available = (await ctx.modelRegistry.getAvailable()).filter(model => model.provider === selected);
        const inScope = ctx.scopedModels.find(item => item.model.provider === selected);
        const model = inScope?.model ?? available.find(model => model.id === ctx.model?.id)
            ?? available.find(model => model.id === "default") ?? available[0];
        if (!model || !await pi.setModel(model)) {
            ctx.ui.notify("The selected DAM login has no usable pi model. Check the Claude Code bridge package or the saved Codex login, then restart this agent.", "error");
            ctx.shutdown();
        } else if (inScope?.thinkingLevel) {
            pi.setThinkingLevel(inScope.thinkingLevel);
        }
    });
}

interface Login {
    provider: string;
    harness: "claude" | "codex";
    name: string;
    authFile: string;
    command: string;
    config: string;
}
const expandHome = (path: string): string => path.replace(/^\$HOME(?=\/)/, homedir());

async function registerLogins(pi: ExtensionAPI, manifestFile: string): Promise<void> {
    const manifest = JSON.parse(await readFile(manifestFile, "utf8")) as { logins: Login[]; legacyProvider?: string };
    if (!Array.isArray(manifest.logins) || manifest.logins.length > 16) throw new Error("Invalid DAM pi login list.");
    const native = builtinProviders().find(provider => provider.id === "openai-codex");
    type Provider = NonNullable<typeof native>;
    let claudeFactory: (() => Provider) | undefined;
    if (manifest.logins.some(login => login.harness === "claude")) {
        try {
            const module = join(process.env.PI_CODING_AGENT_DIR ?? join(homedir(), ".pi", "agent"),
                "git", "github.com", "earendil-works", "pi-claude-code", "src", "provider.ts");
            claudeFactory = (await import(pathToFileURL(module).href)).createClaudeCodeProvider;
        } catch {
            throw new Error("Install git:github.com/earendil-works/pi-claude-code on this Runner before forwarding Claude logins to pi.");
        }
    }
    for (const login of manifest.logins) {
        const source = login.harness === "claude" ? claudeFactory?.() : native;
        if (!source) throw new Error("The selected DAM login needs a pi provider that is not installed.");
        const file = expandHome(login.authFile);
        const environment = {
            PI_CLAUDE_CODE_COMMAND: expandHome(login.command),
            PI_CLAUDE_CODE_CONFIG_DIR: expandHome(login.config),
            CLAUDE_CONFIG_DIR: expandHome(login.config),
            // Suppress inherited competing credentials before the bridge validates its child environment.
            CLAUDE_CODE_OAUTH_TOKEN: "", ANTHROPIC_API_KEY: "", ANTHROPIC_AUTH_TOKEN: "", ANTHROPIC_BASE_URL: "",
            ANTHROPIC_FOUNDRY_API_KEY: "", CLAUDE_CODE_USE_BEDROCK: "", CLAUDE_CODE_USE_VERTEX: "", CLAUDE_CODE_USE_FOUNDRY: "",
        };
        const check = async () => {
            if (login.harness === "codex") await readCodexAccessToken(file);
            else {
                try { if (!(await stat(file)).isFile()) throw new Error(); }
                catch { throw new Error("DAM's Claude login is unavailable. Check My settings → Models, then restart pi."); }
            }
        };
        const models = () => source.getModels().map(model => ({ ...model, provider: login.provider }));
        pi.registerProvider({
            ...source, id: login.provider, name: login.name,
            getModels: models, getAllModels: models,
            // Discovery in the external bridge uses module-wide environment state. Use its built-in model catalog,
            // and isolate every stream explicitly instead of changing process.env or that shared state.
            refreshModels: undefined,
            stream: (model, context, options) => source.stream(model, context, login.harness === "claude"
                ? Object.assign({}, options, { env: { ...options?.env, ...environment } }) : options),
            streamSimple: (model, context, options) => source.streamSimple(model, context, login.harness === "claude"
                ? Object.assign({}, options, { env: { ...options?.env, ...environment } }) : options),
            auth: { apiKey: {
                name: login.name,
                async check() { await check(); return { type: "oauth", source: login.name }; },
                async resolve() {
                    await check();
                    return login.harness === "codex"
                        ? { auth: { apiKey: await readCodexAccessToken(file) }, source: login.name }
                        : { auth: { apiKey: "external-process" }, env: environment, source: login.name };
                },
            } },
        });
    }
    pi.on("session_start", async (_event, ctx) => {
        // pi may already have chosen a fallback when it could not restore the old provider identifier.
        const recorded = ctx.sessionManager?.getBranch().slice().reverse().find(entry => entry.type === "model_change");
        const previous = recorded?.provider ?? ctx.model?.provider;
        if (previous !== "dam-codex" && previous !== "claude-code") return;
        // Only migrate an old explicit preview binding when that exact account is still selected.
        if (!manifest.legacyProvider) return;
        const available = (await ctx.modelRegistry.getAvailable()).filter(model => model.provider === manifest.legacyProvider);
        const scoped = ctx.scopedModels.find(item => item.model.provider === manifest.legacyProvider);
        const model = available.find(model => model.id === (recorded?.modelId ?? ctx.model?.id)) ?? scoped?.model ?? available[0];
        if (!model || !await pi.setModel(model)) {
            ctx.ui.notify("The previous DAM login has no usable pi model. Check My settings → Models.", "error");
            ctx.shutdown();
        } else if (scoped?.thinkingLevel) pi.setThinkingLevel(scoped.thinkingLevel);
    });
}
