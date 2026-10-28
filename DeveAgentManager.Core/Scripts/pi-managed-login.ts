// Loaded only for the explicit DAM managed-login preview. No OAuth refresh credentials are stored in pi.
import { readFile, stat } from "node:fs/promises";
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

export default function managedLogin(pi: ExtensionAPI): void {
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
