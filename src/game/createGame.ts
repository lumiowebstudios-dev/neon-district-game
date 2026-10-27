import * as THREE from "three";
import { ATRIUM, LIGHTS, locationFor, type Spot } from "./blocks";
import { mountDistrict } from "./mount";
import { basis, createSim, EYE, stepSim, type Sim } from "./sim";

export type Hud = {
  location: string;
  prompt: string | null;
  mode: "read" | "hint" | null;
};

export type Detail = { id: string; title: string; body: string };

export type GameHooks = {
  onHud: (hud: Hud) => void;
  onDetail: (detail: Detail | null) => void;
  onPlaying: (playing: boolean) => void;
};

export type GameHandle = {
  dispose: () => void;
  setPlaying: (playing: boolean) => void;
  lock: () => void;
  setMove: (x: number, y: number) => void;
  addLook: (dx: number, dy: number) => void;
  setSprint: (down: boolean) => void;
  interact: () => void;
  closeDetail: () => void;
  setMuted: (muted: boolean) => void;
  setMobile: (mobile: boolean) => void;
};

type Probe = {
  getYaw: () => number;
  getSpeed: () => number;
  getPos: () => { x: number; y: number; z: number };
  setKeys: (codes: string[]) => void;
  setPos: (x: number, z: number) => void;
};

declare global {
  interface Window {
    __controlsTest?: Probe;
  }
}

const GAME_KEYS = new Set([
  "KeyW",
  "KeyA",
  "KeyS",
  "KeyD",
  "ArrowUp",
  "ArrowDown",
  "ArrowLeft",
  "ArrowRight",
  "KeyE",
  "ShiftLeft",
  "ShiftRight",
  "Space",
]);

function clamp(v: number, a: number, b: number) {
  return Math.max(a, Math.min(b, v));
}

function deadzone(x: number, y: number, dz = 0.18) {
  const m = Math.hypot(x, y);
  if (m < dz) return { x: 0, y: 0 };
  const s = (m - dz) / (1 - dz) / m;
  return { x: x * s, y: y * s };
}

export function createGame(canvas: HTMLCanvasElement, hooks: GameHooks): GameHandle {
  const reduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const mobileQuery = window.matchMedia("(pointer: coarse)").matches || window.innerWidth < 860;
  let mobile = mobileQuery;
  let playing = false;
  let muted = false;
  let touchX = 0;
  let touchY = 0;
  let touchSprint = false;
  let pendingYaw = 0;
  let pendingPitch = 0;
  let dragging = false;
  let wasLocked = false;
  const keys = new Set<string>();
  const injected = new Set<string>();
  let ePrev = false;
  let padPrev = false;

  const sim: Sim = createSim(0, 7.4);
  const renderer = new THREE.WebGLRenderer({
    canvas,
    antialias: !mobile,
    alpha: false,
    powerPreference: "high-performance",
    stencil: false,
  });
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = 1.02;
  let dpr = Math.min(window.devicePixelRatio || 1, mobile ? 1.15 : 1.5);
  renderer.setPixelRatio(dpr);

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(0x0b0912);
  scene.fog = new THREE.FogExp2(0x0b0912, 0.013);
  const fog = scene.fog;
  const fogOut = new THREE.Color(0x0b0912);
  const fogIn = new THREE.Color(0x140e16);

  const camera = new THREE.PerspectiveCamera(76, 1, 0.08, 420);
  camera.rotation.order = "YXZ";

  const sky = new THREE.Mesh(
    new THREE.SphereGeometry(280, 24, 16),
    new THREE.ShaderMaterial({
      side: THREE.BackSide,
      depthWrite: false,
      fog: false,
      vertexShader: `
        varying vec3 vP;
        void main() {
          vP = position;
          gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
        }
      `,
      fragmentShader: `
        varying vec3 vP;
        void main() {
          float h = clamp(normalize(vP).y, 0.0, 1.0);
          vec3 zenith = vec3(0.03, 0.025, 0.07);
          vec3 mid = vec3(0.10, 0.05, 0.15);
          vec3 hor = vec3(0.11, 0.055, 0.09);
          vec3 col = mix(hor, mid, smoothstep(0.0, 0.28, h));
          col = mix(col, zenith, smoothstep(0.18, 0.85, h));
          col += vec3(0.05, 0.16, 0.2) * exp(-pow((h - 0.06) * 14.0, 2.0)) * 0.45;
          gl_FragColor = vec4(col, 1.0);
        }
      `,
    }),
  );
  sky.renderOrder = -1;
  sky.frustumCulled = false;
  scene.add(sky);

  scene.add(new THREE.HemisphereLight(0x221838, 0x07070c, 0.42));
  scene.add(new THREE.AmbientLight(0x1c1830, 0.18));
  const moon = new THREE.DirectionalLight(0xc5d2ff, 0.38);
  moon.position.set(-18, 28, 10);
  scene.add(moon);

  const root = new THREE.Group();
  scene.add(root);
  const mounted = mountDistrict(root);
  const colliders = mounted.colliders;
  const spots = mounted.district.spots;

  for (const spec of LIGHTS) {
    const light = new THREE.PointLight(spec.color, spec.intensity, spec.distance, 2);
    light.position.set(spec.x, spec.y, spec.z);
    scene.add(light);
  }

  const moteGeo = new THREE.SphereGeometry(0.045, 8, 8);
  const moteColors = [0x7eebff, 0xffc27a, 0xd4c4ff];
  const motes: Array<{ mesh: THREE.Mesh; x: number; z: number; phase: number; speed: number; base: number }> = [];
  for (let i = 0; i < 9; i++) {
    const mat = new THREE.MeshBasicMaterial({
      color: moteColors[i % 3],
      transparent: true,
      opacity: 0.8,
      blending: THREE.AdditiveBlending,
      depthWrite: false,
      toneMapped: false,
    });
    const mesh = new THREE.Mesh(moteGeo, mat);
    const x = (i % 2 === 0 ? -1 : 1) * (2.2 + (i % 3) * 0.7);
    const z = 12 - i * 3.3;
    mesh.position.set(x, 1.4, z);
    scene.add(mesh);
    motes.push({ mesh, x, z, phase: i * 0.8, speed: 0.6 + (i % 3) * 0.15, base: 1.15 + (i % 4) * 0.22 });
  }

  let audio: AudioContext | null = null;
  let master: GainNode | null = null;

  const ensureAudio = () => {
    if (audio) {
      if (audio.state === "suspended") void audio.resume();
      return;
    }
    const AC = window.AudioContext;
    if (!AC) return;
    audio = new AC();
    master = audio.createGain();
    master.gain.value = muted ? 0 : 0.16;
    master.connect(audio.destination);
    const filter = audio.createBiquadFilter();
    filter.type = "lowpass";
    filter.frequency.value = 220;
    filter.connect(master);
    const bed = audio.createGain();
    bed.gain.value = 0.09;
    bed.connect(filter);
    const o1 = audio.createOscillator();
    o1.type = "sine";
    o1.frequency.value = 55;
    const o2 = audio.createOscillator();
    o2.type = "sine";
    o2.frequency.value = 82.4;
    o1.connect(bed);
    o2.connect(bed);
    o1.start();
    o2.start();
  };

  const blip = (freq: number) => {
    if (!audio || !master || muted) return;
    const o = audio.createOscillator();
    const g = audio.createGain();
    o.type = "triangle";
    o.frequency.value = freq;
    g.gain.setValueAtTime(0.04, audio.currentTime);
    g.gain.exponentialRampToValueAtTime(0.0008, audio.currentTime + 0.28);
    o.connect(g);
    g.connect(master);
    o.start();
    o.stop(audio.currentTime + 0.3);
  };

  const resize = () => {
    const w = canvas.clientWidth || window.innerWidth;
    const h = canvas.clientHeight || window.innerHeight;
    renderer.setPixelRatio(dpr);
    renderer.setSize(w, h, false);
    camera.aspect = w / Math.max(h, 1);
    camera.updateProjectionMatrix();
  };
  resize();

  let slow = 0;
  let fov = 76;
  let last = performance.now();
  let hudKey = "";
  let detailId = "";
  let nearest: Spot | null = null;
  let mode: Hud["mode"] = null;

  const held = (code: string) => injected.has(code) || (playing && keys.has(code));

  const publishHud = (location: string, prompt: string | null, nextMode: Hud["mode"]) => {
    const key = `${location}|${prompt ?? ""}|${nextMode ?? ""}`;
    if (key === hudKey) return;
    hudKey = key;
    hooks.onHud({ location, prompt, mode: nextMode });
  };

  const setDetail = (spot: Spot | null) => {
    const id = spot?.id ?? "";
    if (id === detailId) return;
    detailId = id;
    hooks.onDetail(spot ? { id: spot.id, title: spot.title, body: spot.body } : null);
  };

  const interact = () => {
    if (detailId) {
      setDetail(null);
      return;
    }
    if (mode === "read" && nearest) {
      setDetail(nearest);
      ensureAudio();
      blip(520);
    }
  };

  const frame = (now: number) => {
    const dt = Math.min((now - last) / 1000, 0.05);
    last = now;
    if (dt > 0.034) slow += 1;
    else slow = Math.max(0, slow - 1);
    if (slow > 40 && dpr > 1) {
      dpr = 1;
      resize();
      slow = -1000;
    }

    let padX = 0;
    let padY = 0;
    let padYaw = 0;
    let padPitch = 0;
    let padSprint = false;
    let padInteract = false;
    const pads = navigator.getGamepads?.();
    if (pads) {
      for (const pad of pads) {
        if (!pad) continue;
        const left = deadzone(pad.axes[0] ?? 0, pad.axes[1] ?? 0);
        const right = deadzone(pad.axes[2] ?? 0, pad.axes[3] ?? 0, 0.2);
        padX += left.x;
        padY += -left.y;
        padYaw += -right.x * 1.7 * dt;
        padPitch += -right.y * 1.45 * dt;
        if (pad.buttons[0]?.pressed) padInteract = true;
        if (pad.buttons[7]?.pressed || pad.buttons[5]?.pressed) padSprint = true;
      }
    }

    const keyForward = (held("KeyW") || held("ArrowUp") ? 1 : 0) - (held("KeyS") || held("ArrowDown") ? 1 : 0);
    const keyStrafe = (held("KeyD") || held("ArrowRight") ? 1 : 0) - (held("KeyA") || held("ArrowLeft") ? 1 : 0);
    let forward = keyForward + touchY + padY;
    let strafe = keyStrafe + touchX + padX;
    const mag = Math.hypot(forward, strafe);
    if (mag > 1) {
      forward /= mag;
      strafe /= mag;
    }
    const sprint = held("ShiftLeft") || held("ShiftRight") || touchSprint || padSprint;
    const simulate = playing || injected.size > 0;
    const yawDelta = simulate ? clamp(pendingYaw + padYaw, -0.5, 0.5) : 0;
    const pitchDelta = simulate ? clamp(pendingPitch + padPitch, -0.5, 0.5) : 0;
    pendingYaw = 0;
    pendingPitch = 0;

    if (simulate) {
      stepSim(
        sim,
        { forward, strafe, sprint: sprint && mag > 0.08, yawDelta, pitchDelta },
        dt,
        colliders,
      );
    } else {
      sim.vx = 0;
      sim.vz = 0;
      sim.bob *= Math.exp(-10 * dt);
    }

    const { rx, rz } = basis(sim.yaw);
    const bobAmp = reduced ? 0 : 1;
    const bobY = sim.bob * 0.04 * bobAmp;
    const bobS = Math.cos(sim.walk * 1.05) * 0.012 * Math.min(1, Math.abs(sim.bob)) * bobAmp;
    camera.position.set(sim.x + rx * bobS, EYE + bobY, sim.z + rz * bobS);
    camera.rotation.y = sim.yaw;
    camera.rotation.x = sim.pitch;
    const targetFov = sprint && mag > 0.2 ? 82 : 76;
    fov += (targetFov - fov) * (1 - Math.exp(-6 * dt));
    if (Math.abs(camera.fov - fov) > 0.04) {
      camera.fov = fov;
      camera.updateProjectionMatrix();
    }

    const inside = locationFor(sim.x, sim.z) === "Lumina Atrium";
    const targetDensity = inside ? 0.022 : 0.013;
    fog.density += (targetDensity - fog.density) * (1 - Math.exp(-2.4 * dt));
    fog.color.lerp(inside ? fogIn : fogOut, 1 - Math.exp(-2 * dt));

    if (!reduced) {
      const t = now * 0.001;
      for (const mote of motes) {
        mote.mesh.position.y = mote.base + Math.sin(t * mote.speed + mote.phase) * 0.32;
        mote.mesh.position.x = mote.x + Math.sin(t * 0.35 + mote.phase) * 0.18;
      }
    }

    const location = locationFor(sim.x, sim.z);
    let prompt: string | null = null;
    mode = null;
    nearest = null;
    if (detailId) {
      prompt = "Close";
      mode = "read";
    } else {
      const outer = Math.hypot(sim.x - -7.55, sim.z - ATRIUM.doorZ) < 2.35 && sim.x > ATRIUM.faceX;
      const inner = Math.hypot(sim.x - -11.05, sim.z - ATRIUM.doorZ) < 2.15 && sim.x < ATRIUM.faceX - 0.45;
      if (!inside && outer) {
        prompt = "Enter Lumina Atrium";
        mode = "hint";
      } else if (inside && inner) {
        prompt = "Exit to the promenade";
        mode = "hint";
      } else {
        let best = 1e9;
        for (const spot of spots) {
          const d = Math.hypot(sim.x - spot.x, sim.z - spot.z);
          if (d < spot.radius && d < best) {
            best = d;
            nearest = spot;
          }
        }
        if (nearest) {
          prompt = nearest.prompt;
          mode = "read";
        }
      }
    }
    publishHud(location, prompt, mode);

    const eDown = held("KeyE");
    if ((eDown && !ePrev) || (padInteract && !padPrev)) interact();
    ePrev = eDown;
    padPrev = padInteract;

    renderer.render(scene, camera);
  };

  renderer.setAnimationLoop(frame);
  publishHud(locationFor(sim.x, sim.z), null, null);

  const ac = new AbortController();
  const opt = { signal: ac.signal };

  window.addEventListener(
    "keydown",
    (e) => {
      if (e.code === "Escape" && playing) {
        playing = false;
        keys.clear();
        hooks.onPlaying(false);
        return;
      }
      if (GAME_KEYS.has(e.code)) e.preventDefault();
      keys.add(e.code);
    },
    opt,
  );
  window.addEventListener("keyup", (e) => keys.delete(e.code), opt);
  window.addEventListener("blur", () => keys.clear(), opt);
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) keys.clear();
  }, opt);

  window.addEventListener(
    "mousemove",
    (e) => {
      if (!playing || mobile) return;
      const locked = document.pointerLockElement === canvas;
      if (!locked && !dragging) return;
      pendingYaw -= e.movementX * 0.00215;
      pendingPitch -= e.movementY * 0.00215;
    },
    opt,
  );
  canvas.addEventListener(
    "pointerdown",
    (e) => {
      if (!playing || mobile || e.button !== 0) return;
      dragging = true;
    },
    opt,
  );
  window.addEventListener("pointerup", () => {
    dragging = false;
  }, opt);
  window.addEventListener("pointercancel", () => {
    dragging = false;
  }, opt);

  document.addEventListener(
    "pointerlockchange",
    () => {
      const locked = document.pointerLockElement === canvas;
      if (wasLocked && !locked && playing && !mobile) {
        playing = false;
        keys.clear();
        hooks.onPlaying(false);
      }
      wasLocked = locked;
    },
    opt,
  );

  const onResize = () => resize();
  window.addEventListener("resize", onResize, opt);
  const observer = new ResizeObserver(() => resize());
  observer.observe(canvas);

  const lock = () => {
    if (mobile) return;
    const request = () => {
      try {
        const ret = canvas.requestPointerLock({ unadjustedMovement: true } as PointerLockOptions);
        if (ret && typeof (ret as Promise<void>).catch === "function") {
          void (ret as Promise<void>).catch(() => {
            void canvas.requestPointerLock()?.catch(() => undefined);
          });
        }
      } catch {
        try {
          canvas.requestPointerLock();
        } catch {
          /* drag-look still works */
        }
      }
    };
    request();
  };

  const probe: Probe = {
    getYaw: () => sim.yaw,
    getSpeed: () => Math.hypot(sim.vx, sim.vz),
    getPos: () => ({ x: sim.x, y: 0, z: sim.z }),
    setKeys: (codes: string[]) => {
      injected.clear();
      for (const code of codes) injected.add(code);
    },
    setPos: (x: number, z: number) => {
      sim.x = x;
      sim.z = z;
      sim.vx = 0;
      sim.vz = 0;
    },
  };
  window.__controlsTest = probe;

  return {
    setPlaying: (next) => {
      playing = next;
      if (next) ensureAudio();
      else keys.clear();
    },
    lock,
    setMove: (x, y) => {
      touchX = x;
      touchY = y;
    },
    addLook: (dx, dy) => {
      pendingYaw -= dx * 0.0044;
      pendingPitch -= dy * 0.0044;
    },
    setSprint: (down) => {
      touchSprint = down;
    },
    interact,
    closeDetail: () => setDetail(null),
    setMuted: (next) => {
      muted = next;
      if (master) master.gain.value = next ? 0 : 0.16;
    },
    setMobile: (next) => {
      mobile = next;
    },
    dispose: () => {
      renderer.setAnimationLoop(null);
      ac.abort();
      observer.disconnect();
      if (window.__controlsTest === probe) delete window.__controlsTest;
      scene.traverse((obj) => {
        const mesh = obj as THREE.Mesh;
        if (mesh.geometry) mesh.geometry.dispose();
        const material = mesh.material;
        const list = Array.isArray(material) ? material : material ? [material] : [];
        for (const item of list) {
          const record = item as unknown as Record<string, unknown>;
          for (const value of Object.values(record)) {
            if (value instanceof THREE.Texture) value.dispose();
          }
          item.dispose();
        }
      });
      mounted.dispose();
      renderer.dispose();
      if (audio) void audio.close();
    },
  };
}
