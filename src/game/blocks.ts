import type { Aabb } from "./sim";

export type MatId =
  | "metal"
  | "trim"
  | "plaster"
  | "warm"
  | "sofa"
  | "cushion"
  | "wood"
  | "plant"
  | "planter"
  | "rug"
  | "floor"
  | "cyan"
  | "violet"
  | "amber"
  | "winCyan"
  | "winViolet"
  | "winAmber"
  | "hidden";

export type Kind = "box" | "cyl" | "sphere";

export type Block = {
  x: number;
  y: number;
  z: number;
  w: number;
  h: number;
  d: number;
  mat: MatId;
  collide: boolean;
  kind: Kind;
};

export type Spot = {
  id: string;
  x: number;
  z: number;
  radius: number;
  prompt: string;
  title: string;
  body: string;
};

export type Sign = {
  text: string;
  x: number;
  y: number;
  z: number;
  w: number;
  h: number;
  rotY: number;
  color: string;
};

export type Gutter = { x: number; z: number; tint: "cyan" | "violet" };

export type LightSpec = {
  color: number;
  intensity: number;
  distance: number;
  x: number;
  y: number;
  z: number;
};

export const SPAWN = { x: 0, z: 7.4 };

export const ATRIUM = {
  faceX: -9.05,
  backX: -23.6,
  z0: -4,
  z1: 15,
  doorZ0: 3.2,
  doorZ1: 6.1,
  doorZ: 4.65,
};

export const LIGHTS: LightSpec[] = [
  { color: 0x49e4ff, intensity: 28, distance: 16, x: -14.2, y: 3.15, z: 4.65 },
  { color: 0xffb15a, intensity: 16, distance: 10, x: -17.4, y: 1.7, z: -0.7 },
  { color: 0xffb15a, intensity: 36, distance: 24, x: 0.4, y: 4.4, z: -6.5 },
  { color: 0xc4a6ff, intensity: 26, distance: 15, x: 7.4, y: 3.6, z: 5.1 },
];

export function locationFor(x: number, z: number) {
  const inside =
    x < ATRIUM.faceX - 0.2 &&
    x > ATRIUM.backX + 0.35 &&
    z > ATRIUM.z0 + 0.4 &&
    z < ATRIUM.z1 - 0.4;
  if (inside) return "Lumina Atrium";
  if (z < -15.5 && x > -13 && x < 13) return "Spire Court";
  return "The Promenade";
}

export function collidersFrom(blocks: Block[]): Aabb[] {
  const out: Aabb[] = [];
  for (const b of blocks) {
    if (!b.collide) continue;
    out.push({
      minX: b.x - b.w / 2,
      maxX: b.x + b.w / 2,
      minY: b.y - b.h / 2,
      maxY: b.y + b.h / 2,
      minZ: b.z - b.d / 2,
      maxZ: b.z + b.d / 2,
    });
  }
  return out;
}

export type District = {
  blocks: Block[];
  spots: Spot[];
  signs: Sign[];
  gutters: Gutter[];
};

export function buildDistrict(): District {
  const blocks: Block[] = [];
  const add = (
    x: number,
    y: number,
    z: number,
    w: number,
    h: number,
    d: number,
    mat: MatId,
    collide = true,
    kind: Kind = "box",
  ) => {
    blocks.push({ x, y, z, w, h, d, mat, collide, kind });
  };

  // —— Ground dressings (no collision) ——
  add(0, 0.02, -8, 6.4, 0.03, 46, "floor", false);

  // Lot walls close the block so the street doesn't spill into fog.
  add(0, 2.2, 28.2, 52, 4.4, 0.6, "metal");
  add(0, 2.4, -38, 52, 4.8, 0.6, "metal");
  add(-26.4, 2.1, -5, 0.55, 4.2, 68, "metal");
  add(24.2, 2.1, -5, 0.55, 4.2, 68, "metal");

  // —— Lumina Atrium (enterable) ——
  const t = 0.48;
  const ah = 8.8;
  const ay = ah / 2;
  const eastX = ATRIUM.faceX - t / 2;
  const northLen = ATRIUM.doorZ0 - ATRIUM.z0;
  const northZ = (ATRIUM.z0 + ATRIUM.doorZ0) / 2;
  const southLen = ATRIUM.z1 - ATRIUM.doorZ1;
  const southZ = (ATRIUM.doorZ1 + ATRIUM.z1) / 2;
  add(eastX, ay, northZ, t, ah, northLen, "plaster");
  add(eastX, ay, southZ, t, ah, southLen, "plaster");
  const headerH = ah - 3.2;
  add(eastX, 3.2 + headerH / 2, ATRIUM.doorZ, t, headerH, ATRIUM.doorZ1 - ATRIUM.doorZ0, "plaster");

  const westX = ATRIUM.backX + t / 2;
  const aLen = ATRIUM.z1 - ATRIUM.z0;
  const aZ = (ATRIUM.z0 + ATRIUM.z1) / 2;
  add(westX, ay, aZ, t, ah, aLen, "plaster");
  const innerW = ATRIUM.faceX - t - (ATRIUM.backX + t);
  const innerX = (ATRIUM.faceX - t + ATRIUM.backX + t) / 2;
  add(innerX, ay, ATRIUM.z0 + t / 2, innerW, ah, t, "plaster");
  add(innerX, ay, ATRIUM.z1 - t / 2, innerW, ah, t, "plaster");

  // Curtain wall proud of the street face. Door gap stays open.
  add(ATRIUM.faceX + 0.05, ay, northZ, 0.08, ah * 0.94, northLen * 0.92, "winCyan", false);
  add(ATRIUM.faceX + 0.05, ay, southZ, 0.08, ah * 0.94, southLen * 0.92, "winCyan", false);
  add(
    ATRIUM.faceX + 0.05,
    3.2 + headerH / 2,
    ATRIUM.doorZ,
    0.08,
    headerH * 0.9,
    (ATRIUM.doorZ1 - ATRIUM.doorZ0) * 0.96,
    "winCyan",
    false,
  );

  add(ATRIUM.faceX + 0.16, 3.62, aZ, 0.16, 0.18, aLen * 0.98, "cyan", false);
  add(8.78, 3.45, 6.5, 0.18, 0.18, 16.2, "violet", false);
  add(0, 3.15, -20.78, 22, 0.2, 0.16, "amber", false);
  add(-8.15, 3.32, ATRIUM.doorZ, 1.7, 0.1, 3.35, "cyan", false);
  add(-8.55, 0.025, ATRIUM.doorZ, 1.15, 0.02, 2.7, "cyan", false);

  // Interior shell.
  const ceilY = 4.42;
  add(innerX, 0.025, aZ, innerW - 0.3, 0.03, aLen - 1.1, "floor", false);
  add(innerX, ceilY, aZ, innerW - 0.15, 0.14, aLen - 0.7, "trim", false);
  add(innerX, 4.22, ATRIUM.doorZ, innerW - 1.4, 0.06, 0.28, "cyan", false);

  // Nave sculpture, offset off the centerline so the door path stays clear.
  add(-16.8, 0.28, 3.4, 0.72, 0.56, 0.72, "trim");
  add(-16.8, 1.35, 3.4, 0.07, 1.7, 0.07, "cyan", false);
  add(-16.8, 2.15, 3.4, 0.22, 0.22, 0.22, "cyan", false, "sphere");

  // Lounge, north of the entry axis.
  add(-19.15, 0.32, -0.85, 1.05, 0.55, 2.7, "sofa");
  add(-19.15, 0.66, -0.85, 0.92, 0.12, 2.45, "cushion", false);
  add(-15.45, 0.32, -0.85, 1.05, 0.55, 2.7, "sofa");
  add(-15.45, 0.66, -0.85, 0.92, 0.12, 2.45, "cushion", false);
  add(-17.3, 0.26, -0.85, 1.25, 0.4, 1.15, "wood");
  add(-17.3, 0.5, -0.85, 0.35, 0.08, 0.35, "amber", false);
  add(-17.3, 0.03, -0.85, 3.3, 0.02, 4.1, "rug", false);

  // Reception, south of the axis.
  add(-16.2, 0.52, 11.1, 3.5, 1.04, 1.15, "trim");
  add(-16.2, 1.08, 11.1, 3.55, 0.06, 1.22, "cyan", false);

  // Stairs imply a mezzanine without blocking the nave.
  add(-21.7, 0.7, 12.4, 2.3, 1.4, 3.1, "warm");
  add(-20.2, 2.35, 13.15, 3.6, 0.12, 1.5, "trim", false);

  // Columns and planters.
  add(-11.35, 2.15, 0.15, 0.46, 4.3, 0.46, "trim");
  add(-11.35, 2.15, 9.7, 0.46, 4.3, 0.46, "trim");
  add(-12.4, 0.32, 13.2, 0.7, 0.64, 0.7, "planter", true, "cyl");
  add(-12.4, 0.95, 13.2, 0.62, 0.62, 0.62, "plant", false, "sphere");
  add(-12.2, 0.32, -2.4, 0.7, 0.64, 0.7, "planter", true, "cyl");
  add(-12.2, 0.95, -2.4, 0.62, 0.62, 0.62, "plant", false, "sphere");

  // Corner blades.
  add(ATRIUM.faceX + 0.08, 4.6, ATRIUM.z0 + 0.08, 0.1, 8.2, 0.1, "cyan", false);
  add(ATRIUM.faceX + 0.08, 4.6, ATRIUM.z1 - 0.08, 0.1, 8.2, 0.1, "cyan", false);

  // West service block joins the atrium to the spire and the lot wall.
  add(-17.6, 2.7, -12.2, 17.2, 5.4, 16.4, "metal");
  add(-9.02, 2.7, -12.2, 0.08, 4.9, 14.8, "winCyan", false);

  // —— Violet Exchange (sealed) ——
  add(14.42, 3.3, 6.5, 10.75, 6.6, 17, "metal");
  add(15.1, 9.2, 6.6, 7.4, 5.2, 12.2, "trim", false);
  add(15.55, 14.15, 7.1, 4.4, 4.6, 7.6, "metal", false);
  add(9.0, 3.15, 6.5, 0.08, 6.05, 15.6, "winViolet", false);
  add(11.2, 8.6, 6.6, 0.06, 3.6, 10.4, "winViolet", false);
  add(8.15, 3.22, 5.2, 1.85, 0.12, 3.5, "violet", false);
  add(9.02, 1.45, 5.2, 0.06, 2.5, 1.7, "trim", false);
  add(8.95, 5.2, -1.85, 0.12, 9.2, 0.12, "violet", false);

  // East annex toward the spire, sealed to the lot wall.
  add(16.55, 2.45, -11.5, 15.1, 4.9, 18.6, "metal");
  add(9.02, 2.45, -11.5, 0.08, 4.4, 16.6, "winViolet", false);

  // —— Amber Spire ——
  add(0, 1.5, -27.5, 14.6, 3, 13, "metal");
  add(-10.15, 1.65, -27.5, 6.1, 3.3, 13, "trim");
  add(10.15, 1.65, -27.5, 6.1, 3.3, 13, "trim");
  add(0, 1.55, -20.9, 24.2, 2.7, 0.08, "winAmber", false);
  add(0, 11.2, -27.5, 4.5, 16.4, 4.5, "metal", false);
  add(0, 11.2, -25.18, 4.15, 15.2, 0.08, "winAmber", false);
  add(0, 20.6, -27.5, 2.3, 4.8, 2.3, "trim", false);
  add(0, 24.4, -27.5, 0.16, 6.4, 0.16, "amber", false);
  add(0, 0.75, -18.75, 1.2, 1.5, 0.16, "trim");

  // —— South gallery, behind the opening view ——
  add(0, 3.1, 20.6, 32, 6.2, 11.1, "metal");
  add(0, 3.05, 14.95, 29.5, 5.5, 0.08, "winCyan", false);

  // Street furniture.
  const lamps: Array<[number, number]> = [
    [-6.35, 13.2],
    [6.35, 1.1],
    [-6.35, -6.8],
    [6.35, -14.6],
  ];
  for (const [lx, lz] of lamps) {
    add(lx, 1.7, lz, 0.14, 3.4, 0.14, "trim");
    add(lx, 3.5, lz, 0.42, 0.42, 0.42, "amber", false, "sphere");
  }

  add(5.2, 0.85, 11.45, 0.62, 1.7, 0.42, "trim");
  add(5.2, 1.72, 11.45, 0.7, 0.08, 0.5, "cyan", false);

  add(-4.7, 0.42, 13.1, 1.5, 0.42, 0.55, "wood");
  add(3.6, 0.42, -2.4, 1.5, 0.42, 0.55, "wood");

  // Planters along the promenade.
  add(-5.4, 0.28, 3.15, 0.55, 0.56, 0.55, "planter", true, "cyl");
  add(-5.4, 0.78, 3.15, 0.48, 0.48, 0.48, "plant", false, "sphere");
  add(5.5, 0.28, -8.4, 0.55, 0.56, 0.55, "planter", true, "cyl");
  add(5.5, 0.78, -8.4, 0.48, 0.48, 0.48, "plant", false, "sphere");

  const gutters: Gutter[] = [];
  for (let z = 14.2; z >= -19.2; z -= 2.15) {
    gutters.push({ x: -6.55, z, tint: "cyan" });
    gutters.push({ x: 6.55, z, tint: "violet" });
  }

  const signs: Sign[] = [
    { text: "LUMINA", x: -8.55, y: 4.85, z: ATRIUM.doorZ, w: 4.4, h: 1.05, rotY: Math.PI / 2, color: "#7eebff" },
    { text: "VIOLET", x: 8.55, y: 5.35, z: 5.2, w: 4.6, h: 1.05, rotY: -Math.PI / 2, color: "#d4c4ff" },
    { text: "AMBER", x: 0, y: 8.2, z: -24.7, w: 4.8, h: 1.25, rotY: 0, color: "#ffc27a" },
    { text: "GALLERY", x: 0, y: 4.15, z: 14.72, w: 4.2, h: 0.7, rotY: Math.PI, color: "#9ad8ff" },
    { text: "GUIDE", x: 5.2, y: 1.15, z: 11.18, w: 0.52, h: 0.7, rotY: Math.PI, color: "#7eebff" },
    { text: "STILL WATER", x: -23.05, y: 2.35, z: 1.2, w: 2.6, h: 1.7, rotY: Math.PI / 2, color: "#d7fbff" },
  ];

  const spots: Spot[] = [
    {
      id: "guide",
      x: 5.2,
      z: 11.45,
      radius: 1.85,
      prompt: "Read the district guide",
      title: "District guide",
      body: "The promenade is private after dusk. Lumina keeps its doors open. Violet seals the exchange until sunrise. Amber is a landmark, not a door.",
    },
    {
      id: "violet",
      x: 7.35,
      z: 5.2,
      radius: 2.05,
      prompt: "Check the exchange doors",
      title: "Violet Exchange",
      body: "Members only tonight. The glass is warm. The lock is not. Come back when the sign stops humming.",
    },
    {
      id: "sculpture",
      x: -16.8,
      z: 3.4,
      radius: 1.7,
      prompt: "Inspect the installation",
      title: "Still Water",
      body: "A filament tuned to the street's hum. The concierge used to say it only listens to people who stop walking.",
    },
    {
      id: "desk",
      x: -14.3,
      z: 11.1,
      radius: 1.55,
      prompt: "Read the concierge note",
      title: "Lumina desk",
      body: "Doors stay open. Names stay out of the book. If you are cold, the lounge is north of the nave — the amber side.",
    },
    {
      id: "spire",
      x: 0,
      z: -18.75,
      radius: 1.9,
      prompt: "Read the spire plaque",
      title: "Amber Spire",
      body: "Raised as a navigation fire for the old air lanes. It still burns so the district can find itself in the fog.",
    },
  ];

  return { blocks, spots, signs, gutters };
}
