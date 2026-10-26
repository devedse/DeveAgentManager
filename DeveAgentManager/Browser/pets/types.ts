export interface PetFrame {
    time: number; motion: string; elapsed: number; gaze: { x: number; y: number };
    animate: boolean; showRig: boolean; progress: number; transitioning: boolean;
}
export interface RendererOptions { small?: boolean; }
export interface PetRenderer {
    setStage(stage: number): void;
    pose(frame: PetFrame): { rootX: number; lift: number } | null;
    dispose?(): void;
}
export interface PetArtModule {
    prepare?(): Promise<void>;
    createRenderer(mount: HTMLElement, options: RendererOptions): PetRenderer;
}
