// External JavaScript library and .NET interop boundaries. Application modules retain their inferred types.
interface Window {
    damNotificationSounds: any;
    webkitAudioContext: typeof AudioContext;
    damTerminalLinks: any;
    damTerminal: any;
    damTheme: any;
    damUi: any;
    damAttention: any;
}
declare const XtermBlazor: any;
declare const FitAddon: any;
declare const WebLinksAddon: any;
declare const damTerminalLinks: any;
declare var DamTouchInput: any;

interface DotNetReference {
    invokeMethodAsync<T = void>(method: string, ...args: unknown[]): Promise<T>;
}
