/** First-person sim. Yaw 0 looks down world −Z. A strafes left, D strafes right. */

export const RADIUS = 0.34;
export const EYE = 1.64;
export const BODY = 1.72;
export const WALK = 4.35;
export const SPRINT = 7.05;
export const PITCH_LIMIT = 1.42;

export type Aabb = {
  minX: number;
  maxX: number;
  minY: number;
  maxY: number;
  minZ: number;
  maxZ: number;
};

export type Sim = {
  x: number;
  z: number;
  yaw: number;
  pitch: number;
  vx: number;
  vz: number;
  walk: number;
  bob: number;
};

export type MoveInput = {
  forward: number;
  strafe: number;
  sprint: boolean;
  yawDelta: number;
  pitchDelta: number;
};

export function basis(yaw: number) {
  return {
    fx: -Math.sin(yaw),
    fz: -Math.cos(yaw),
    rx: Math.cos(yaw),
    rz: -Math.sin(yaw),
  };
}

export function createSim(x: number, z: number): Sim {
  return { x, z, yaw: 0, pitch: -0.08, vx: 0, vz: 0, walk: 0, bob: 0 };
}

export function hits(x: number, z: number, boxes: Aabb[], radius = RADIUS, body = BODY) {
  for (let i = 0; i < boxes.length; i++) {
    const b = boxes[i];
    if (b.maxY < 0.4 || b.minY > body) continue;
    if (x + radius > b.minX && x - radius < b.maxX && z + radius > b.minZ && z - radius < b.maxZ) {
      return true;
    }
  }
  return false;
}

function clamp(v: number, a: number, b: number) {
  return Math.max(a, Math.min(b, v));
}

export function stepSim(sim: Sim, input: MoveInput, dt: number, boxes: Aabb[]) {
  const dtC = Math.min(Math.max(dt, 0), 0.05);
  sim.yaw += input.yawDelta;
  sim.pitch = clamp(sim.pitch + input.pitchDelta, -PITCH_LIMIT, PITCH_LIMIT);

  const { fx, fz, rx, rz } = basis(sim.yaw);
  let wishX = fx * input.forward + rx * input.strafe;
  let wishZ = fz * input.forward + rz * input.strafe;
  const mag = Math.hypot(wishX, wishZ);
  if (mag > 1) {
    wishX /= mag;
    wishZ /= mag;
  }

  const moving = mag > 0.08;
  const speed = moving ? (input.sprint ? SPRINT : WALK) : 0;
  const tx = (mag > 1 ? wishX : wishX) * speed;
  const tz = (mag > 1 ? wishZ : wishZ) * speed;
  const k = 1 - Math.exp(-12 * dtC);
  sim.vx += (tx - sim.vx) * k;
  sim.vz += (tz - sim.vz) * k;
  if (!moving && Math.hypot(sim.vx, sim.vz) < 0.06) {
    sim.vx = 0;
    sim.vz = 0;
  }

  const dx = sim.vx * dtC;
  const dz = sim.vz * dtC;
  const sub = Math.max(1, Math.ceil(Math.hypot(dx, dz) / 0.16));
  for (let i = 0; i < sub; i++) {
    const sx = dx / sub;
    const sz = dz / sub;
    if (!hits(sim.x + sx, sim.z, boxes)) sim.x += sx;
    else sim.vx = 0;
    if (!hits(sim.x, sim.z + sz, boxes)) sim.z += sz;
    else sim.vz = 0;
  }

  const planar = Math.hypot(sim.vx, sim.vz);
  if (planar > 0.4) {
    sim.walk += planar * dtC;
    sim.bob = Math.sin(sim.walk * 2.15);
  } else {
    sim.bob *= Math.exp(-10 * dtC);
  }
}
