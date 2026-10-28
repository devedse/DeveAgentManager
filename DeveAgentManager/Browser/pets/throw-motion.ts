export interface Velocity { x: number; y: number; }
export interface ThrowBounds { left: number; top: number; right: number; bottom: number; }
export interface PetSlide { x: number; y: number; vx: number; vy: number; }
export interface PetFlight extends PetSlide { floor: number; }
export const THROW_SPEED = 700;

// Use only the end of the gesture: dragging and then holding still is a drop.
export class PointerVelocity {
    private samples: { x: number; y: number; at: number }[] = [];
    add(x: number, y: number, at: number): void {
        this.samples.push({ x, y, at });
        // Keep the point just before the window too, so slower pointer events still have a direction.
        while (this.samples.length > 2 && this.samples[1].at <= at - 100) this.samples.shift();
        this.samples = this.samples.slice(-20);
    }
    release(x: number, y: number, at: number): Velocity {
        this.add(x, y, at);
        const first = this.samples[0], next = this.samples[1], start = Math.max(first.at, at - 100);
        const seconds = (at - start) / 1000;
        if (seconds < .008) return { x: 0, y: 0 };
        const fraction = next.at > first.at ? (start - first.at) / (next.at - first.at) : 0;
        const startX = first.x + (next.x - first.x) * fraction, startY = first.y + (next.y - first.y) * fraction;
        return { x: (x - startX) / seconds, y: (y - startY) / seconds };
    }
}

export function beginThrow(x: number, y: number, velocity: Velocity, size: number, bounds: ThrowBounds): PetFlight | null {
    const speed = Math.hypot(velocity.x, velocity.y);
    // Every upward angle can throw, but only a deliberate fast flick lifts off.
    if (velocity.y >= 0 || speed < THROW_SPEED) return null;
    const scale = Math.min(1, 1500 / speed);
    // A little virtual floor below the release keeps the pet near its chosen position.
    return { x, y, vx: velocity.x * scale, vy: velocity.y * scale, floor: Math.min(bounds.bottom, y + size * .75) };
}

export function beginSlide(x: number, y: number, velocity: Velocity): PetSlide | null {
    const speed = Math.hypot(velocity.x, velocity.y);
    if (speed < 40) return null;
    const scale = Math.min(1, 1500 / speed);
    return { x, y, vx: velocity.x * scale, vy: velocity.y * scale };
}

export function advanceSlide(slide: PetSlide, seconds: number, bounds: ThrowBounds): boolean {
    const dt = Math.max(0, Math.min(.05, seconds)), friction = 1.8, drag = Math.exp(-friction * dt);
    // The shove stays on one plane: both axes coast and slow equally, without gravity.
    slide.x += slide.vx * (1 - drag) / friction; slide.y += slide.vy * (1 - drag) / friction;
    slide.vx *= drag; slide.vy *= drag;
    if (slide.x < bounds.left || slide.x > bounds.right) {
        slide.x = Math.max(bounds.left, Math.min(bounds.right, slide.x)); slide.vx = 0;
    }
    if (slide.y < bounds.top || slide.y > bounds.bottom) {
        slide.y = Math.max(bounds.top, Math.min(bounds.bottom, slide.y)); slide.vy = 0;
    }
    return Math.hypot(slide.vx, slide.vy) < 18;
}

export function advanceThrow(flight: PetFlight, seconds: number, bounds: ThrowBounds): boolean {
    const dt = Math.max(0, Math.min(.05, seconds)), drag = Math.exp(-.8 * dt);
    flight.x += flight.vx * (1 - drag) / .8;
    flight.vx *= drag;
    flight.y += flight.vy * dt + .5 * 1600 * dt * dt;
    flight.vy += 1600 * dt;
    if (flight.x < bounds.left || flight.x > bounds.right) {
        flight.x = Math.max(bounds.left, Math.min(bounds.right, flight.x));
        flight.vx *= -.28;
    }
    if (flight.y < bounds.top) { flight.y = bounds.top; flight.vy = Math.max(0, flight.vy); }
    flight.floor = Math.max(bounds.top, Math.min(bounds.bottom, flight.floor));
    if (flight.y >= flight.floor && flight.vy >= 0) { flight.y = flight.floor; return true; }
    return false;
}
