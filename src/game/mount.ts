import * as THREE from "three";
import { buildDistrict, collidersFrom, type Block, type District, type MatId, type Sign } from "./blocks";
import type { Aabb } from "./sim";

export type Mounted = {
  colliders: Aabb[];
  district: District;
  dispose: () => void;
};

function seeded(seed: number) {
  let n = seed >>> 0;
  return () => {
    n = (Math.imul(n, 1664525) + 1013904223) >>> 0;
    return n / 4294967296;
  };
}

function canvasTex(c: HTMLCanvasElement) {
  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  tex.anisotropy = 4;
  tex.magFilter = THREE.LinearFilter;
  tex.minFilter = THREE.LinearMipmapLinearFilter;
  tex.needsUpdate = true;
  return tex;
}

function makeWindow(primary: string, warm: string, seed: number) {
  const c = document.createElement("canvas");
  c.width = 128;
  c.height = 256;
  const g = c.getContext("2d");
  if (!g) return canvasTex(c);
  const rand = seeded(seed);
  g.fillStyle = "#05070c";
  g.fillRect(0, 0, 128, 256);
  const cols = 3;
  const rows = 6;
  const gap = 8;
  const pad = 8;
  const cw = (128 - pad * 2 - gap * (cols - 1)) / cols;
  const ch = (256 - pad * 2 - gap * (rows - 1)) / rows;
  for (let y = 0; y < rows; y++) {
    for (let x = 0; x < cols; x++) {
      const roll = rand();
      const px = pad + x * (cw + gap);
      const py = pad + y * (ch + gap);
      if (roll < 0.22) {
        g.globalAlpha = 1;
        g.fillStyle = "#10131a";
        g.fillRect(px, py, cw, ch);
      } else {
        g.globalAlpha = 0.75 + rand() * 0.25;
        g.fillStyle = roll > 0.88 ? warm : primary;
        g.fillRect(px, py, cw, ch);
      }
    }
  }
  g.globalAlpha = 1;
  return canvasTex(c);
}

function makeAsphalt() {
  const c = document.createElement("canvas");
  c.width = 256;
  c.height = 256;
  const g = c.getContext("2d");
  if (!g) return canvasTex(c);
  g.fillStyle = "#12141c";
  g.fillRect(0, 0, 256, 256);
  const rand = seeded(19);
  for (let i = 0; i < 1600; i++) {
    const v = 14 + rand() * 26;
    g.fillStyle = `rgb(${v},${v + 1},${v + 6})`;
    g.fillRect(rand() * 256, rand() * 256, 1.4, 1.4);
  }
  g.strokeStyle = "rgba(0,0,0,0.45)";
  g.lineWidth = 1;
  for (let i = 0; i < 7; i++) {
    g.beginPath();
    let x = rand() * 256;
    let y = rand() * 256;
    g.moveTo(x, y);
    for (let k = 0; k < 5; k++) {
      x += (rand() - 0.5) * 48;
      y += (rand() - 0.5) * 48;
      g.lineTo(x, y);
    }
    g.stroke();
  }
  const tex = canvasTex(c);
  tex.wrapS = THREE.RepeatWrapping;
  tex.wrapT = THREE.RepeatWrapping;
  tex.repeat.set(10, 10);
  return tex;
}

function paintSign(text: string, color: string, art: boolean) {
  const c = document.createElement("canvas");
  c.width = 512;
  c.height = art ? 340 : 180;
  const g = c.getContext("2d");
  if (!g) return c;
  g.clearRect(0, 0, c.width, c.height);
  g.fillStyle = "rgba(6,7,12,0.72)";
  g.fillRect(0, 0, c.width, c.height);
  g.strokeStyle = color;
  g.lineWidth = 6;
  g.strokeRect(12, 12, c.width - 24, c.height - 24);
  if (art) {
    g.strokeStyle = color;
    g.globalAlpha = 0.85;
    g.lineWidth = 2;
    g.beginPath();
    g.moveTo(70, 260);
    g.bezierCurveTo(140, 80, 220, 300, 280, 120);
    g.bezierCurveTo(330, 40, 400, 200, 450, 90);
    g.stroke();
    g.globalAlpha = 0.35;
    g.beginPath();
    g.moveTo(60, 200);
    g.bezierCurveTo(160, 160, 240, 240, 450, 170);
    g.stroke();
    g.globalAlpha = 1;
    g.fillStyle = color;
    g.font = "700 42px Syne, sans-serif";
    g.textAlign = "left";
    g.textBaseline = "alphabetic";
    g.fillText(text, 40, 64);
  } else {
    g.fillStyle = color;
    g.font = "800 78px Syne, sans-serif";
    g.textAlign = "center";
    g.textBaseline = "middle";
    g.fillText(text, c.width / 2, c.height / 2 + 4);
  }
  return c;
}

function geometryFor(b: Block) {
  if (b.kind === "cyl") return new THREE.CylinderGeometry(b.w / 2, b.w / 2, b.h, 18);
  if (b.kind === "sphere") return new THREE.SphereGeometry(b.w / 2, 16, 12);
  return new THREE.BoxGeometry(b.w, b.h, b.d);
}

export function mountDistrict(parent: THREE.Object3D): Mounted {
  const district = buildDistrict();
  const asphalt = makeAsphalt();
  const winCyan = makeWindow("#7eebff", "#ffc27a", 3);
  const winViolet = makeWindow("#c4b5fd", "#7eebff", 11);
  const winAmber = makeWindow("#ffc27a", "#fff1d6", 29);

  const standard = (
    color: number,
    roughness: number,
    metalness: number,
    extra: Partial<THREE.MeshStandardMaterialParameters> = {},
  ) =>
    new THREE.MeshStandardMaterial({
      color,
      roughness,
      metalness,
      ...extra,
    });

  const glow = (color: number, intensity: number) =>
    new THREE.MeshStandardMaterial({
      color: 0x07080c,
      emissive: color,
      emissiveIntensity: intensity,
      roughness: 0.32,
      metalness: 0.05,
      toneMapped: false,
    });

  const mats: Record<Exclude<MatId, "hidden">, THREE.Material> = {
    metal: standard(0x10131a, 0.5, 0.62),
    trim: standard(0x1a1e28, 0.42, 0.55),
    plaster: standard(0x1c1a24, 0.92, 0.02),
    warm: standard(0x2a2430, 0.84, 0.06),
    sofa: standard(0x2c2636, 0.78, 0.08),
    cushion: standard(0x3c3650, 0.7, 0.05),
    wood: standard(0x3a2c24, 0.62, 0.12),
    plant: standard(0x143432, 0.7, 0.05, { emissive: 0x0c3330, emissiveIntensity: 0.35 }),
    planter: standard(0x14161e, 0.55, 0.4),
    rug: standard(0x1a1024, 0.95, 0),
    floor: standard(0x141824, 0.2, 0.72),
    cyan: glow(0x3ee0ff, 1.8),
    violet: glow(0xb794ff, 1.7),
    amber: glow(0xffb15a, 1.85),
    winCyan: standard(0x05070c, 0.45, 0.08, {
      map: winCyan,
      emissive: 0xffffff,
      emissiveMap: winCyan,
      emissiveIntensity: 2.6,
      toneMapped: false,
    }),
    winViolet: standard(0x05070c, 0.45, 0.08, {
      map: winViolet,
      emissive: 0xffffff,
      emissiveMap: winViolet,
      emissiveIntensity: 2.5,
      toneMapped: false,
    }),
    winAmber: standard(0x05070c, 0.42, 0.08, {
      map: winAmber,
      emissive: 0xffffff,
      emissiveMap: winAmber,
      emissiveIntensity: 2.7,
      toneMapped: false,
    }),
  };

  const ground = new THREE.Mesh(
    new THREE.PlaneGeometry(84, 78),
    new THREE.MeshStandardMaterial({ map: asphalt, color: 0xffffff, roughness: 0.94, metalness: 0.04 }),
  );
  ground.rotation.x = -Math.PI / 2;
  ground.position.set(0, 0, -6);
  parent.add(ground);

  const ring = new THREE.Mesh(
    new THREE.RingGeometry(1.55, 1.72, 48),
    glow(0x3ee0ff, 1.15),
  );
  ring.rotation.x = -Math.PI / 2;
  ring.position.set(0, 0.02, 7.4);
  parent.add(ring);

  for (const b of district.blocks) {
    if (b.mat === "hidden") continue;
    const mesh = new THREE.Mesh(geometryFor(b), mats[b.mat]);
    mesh.position.set(b.x, b.y, b.z);
    parent.add(mesh);
  }

  const cyanGut = district.gutters.filter((g) => g.tint === "cyan");
  const violetGut = district.gutters.filter((g) => g.tint === "violet");
  const gutterGeo = new THREE.BoxGeometry(0.18, 0.05, 1.65);
  const placeGutters = (list: typeof cyanGut, mat: THREE.Material) => {
    if (!list.length) return;
    const mesh = new THREE.InstancedMesh(gutterGeo, mat, list.length);
    const dummy = new THREE.Object3D();
    list.forEach((g, i) => {
      dummy.position.set(g.x, 0.035, g.z);
      dummy.updateMatrix();
      mesh.setMatrixAt(i, dummy.matrix);
    });
    mesh.instanceMatrix.needsUpdate = true;
    parent.add(mesh);
  };
  placeGutters(cyanGut, mats.cyan);
  placeGutters(violetGut, mats.violet);

  const ringMat = glow(0xffb15a, 2.1);
  const torus = new THREE.TorusGeometry(2.55, 0.055, 8, 48);
  for (const y of [7.2, 11.2, 15.2, 19.1]) {
    const halo = new THREE.Mesh(torus, ringMat);
    halo.rotation.x = Math.PI / 2;
    halo.position.set(0, y, -27.5);
    parent.add(halo);
  }

  const signMeshes: Array<{ canvas: HTMLCanvasElement; tex: THREE.CanvasTexture; art: boolean; text: string; color: string }> = [];
  const addSign = (sign: Sign) => {
    const art = sign.text === "STILL WATER";
    const canvas = paintSign(sign.text, sign.color, art);
    const tex = canvasTex(canvas);
    signMeshes.push({ canvas, tex, art, text: sign.text, color: sign.color });
    const nx = Math.sin(sign.rotY);
    const nz = Math.cos(sign.rotY);
    const glowMat = new THREE.MeshBasicMaterial({
      color: new THREE.Color(sign.color),
      transparent: true,
      opacity: 0.42,
      blending: THREE.AdditiveBlending,
      depthWrite: false,
      toneMapped: false,
    });
    const halo = new THREE.Mesh(new THREE.PlaneGeometry(sign.w * 1.45, sign.h * 1.9), glowMat);
    halo.position.set(sign.x - nx * 0.05, sign.y, sign.z - nz * 0.05);
    halo.rotation.y = sign.rotY;
    parent.add(halo);
    const panel = new THREE.Mesh(
      new THREE.PlaneGeometry(sign.w, sign.h),
      new THREE.MeshBasicMaterial({ map: tex, transparent: true, depthWrite: true, toneMapped: false }),
    );
    panel.position.set(sign.x, sign.y, sign.z);
    panel.rotation.y = sign.rotY;
    parent.add(panel);
  };
  for (const sign of district.signs) addSign(sign);

  const repaint = () => {
    for (const s of signMeshes) {
      const next = paintSign(s.text, s.color, s.art);
      const g = s.canvas.getContext("2d");
      const ng = next.getContext("2d");
      if (g && ng) g.drawImage(next, 0, 0);
      s.tex.needsUpdate = true;
    }
  };
  void document.fonts?.ready.then(repaint);

  return {
    colliders: collidersFrom(district.blocks),
    district,
    dispose: () => {
      asphalt.dispose();
      winCyan.dispose();
      winViolet.dispose();
      winAmber.dispose();
      for (const m of Object.values(mats)) m.dispose();
      for (const s of signMeshes) s.tex.dispose();
    },
  };
}
