export interface Velocity { x: number; y: number; }
export interface ThrowBounds { left: number; top: number; right: number; bottom: number; }
export interface PetFlight { x: number; y: number; vx: number; vy: number; floor: number; }

// Use only the end of the gesture: dragging and then holding still is a drop.
export class PointerVelocity {
    private samples: { x: number; y: number; at: number }[] = [];
    add(x: number, y: number, at: number): void {
        this.samples.push({ x, y, at });
        this.samples = this.samples.filter(sample => at - sample.at <= 100).slice(-20);
    }
    release(x: number, y: number, at: number): Velocity {
        this.add(x, y, at);
        const first = this.samples[0], seconds = (at - first.at) / 1000;
        if (seconds < .008) return { x: 0, y: 0 };
        return { x: (x - first.x) / seconds, y: (y - first.y) / seconds };
    }
}

export function beginThrow(x: number, y: number, velocity: Velocity, size: number, bounds: ThrowBounds): PetFlight | null {
    const speed = Math.hypot(velocity.x, velocity.y);
    if (speed < 220) return null;
    const scale = Math.min(1, 1500 / speed);
    // A little virtual floor below the release keeps the pet near its chosen position.
    return { x, y, vx: velocity.x * scale, vy: velocity.y * scale, floor: Math.min(bounds.bottom, y + size * .75) };
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
