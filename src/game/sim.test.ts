import assert from "node:assert/strict";
import test from "node:test";
import { ATRIUM, buildDistrict, collidersFrom, SPAWN } from "./blocks.ts";
import { basis, createSim, hits, stepSim } from "./sim.ts";

const boxes = collidersFrom(buildDistrict().blocks);

test("yaw 0 faces -Z and right is +X", () => {
  const b = basis(0);
  assert.ok(Math.abs(b.fx) < 1e-6);
  assert.ok(b.fz < -0.9);
  assert.ok(b.rx > 0.9);
});

test("spawn is clear", () => {
  assert.equal(hits(SPAWN.x, SPAWN.z, boxes), false);
});

test("W moves toward -Z", () => {
  const sim = createSim(SPAWN.x, SPAWN.z);
  for (let i = 0; i < 60; i++) {
    stepSim(sim, { forward: 1, strafe: 0, sprint: false, yawDelta: 0, pitchDelta: 0 }, 1 / 60, boxes);
  }
  assert.ok(sim.z < SPAWN.z - 1, `z ${sim.z}`);
  assert.ok(Math.hypot(sim.vx, sim.vz) > 1);
  assert.ok(Math.abs(sim.x) < 0.4);
});

test("A strafes left and D strafes right while facing -Z", () => {
  const left = createSim(SPAWN.x, SPAWN.z);
  const right = createSim(SPAWN.x, SPAWN.z);
  for (let i = 0; i < 45; i++) {
    stepSim(left, { forward: 0, strafe: -1, sprint: false, yawDelta: 0, pitchDelta: 0 }, 1 / 60, boxes);
    stepSim(right, { forward: 0, strafe: 1, sprint: false, yawDelta: 0, pitchDelta: 0 }, 1 / 60, boxes);
  }
  assert.ok(left.x < -1, `A x ${left.x}`);
  assert.ok(right.x > 1, `D x ${right.x}`);
});

test("violet exchange blocks entry", () => {
  const sim = createSim(6, 5.2);
  for (let i = 0; i < 180; i++) {
    stepSim(sim, { forward: 0, strafe: 1, sprint: true, yawDelta: 0, pitchDelta: 0 }, 1 / 60, boxes);
  }
  assert.ok(sim.x < 8.95, `penetrated violet x=${sim.x}`);
  assert.ok(sim.x > 8.2, `stopped too early x=${sim.x}`);
});

test("atrium door can be walked through", () => {
  assert.equal(hits(-8.4, ATRIUM.doorZ, boxes), false);
  const sim = createSim(-6.5, ATRIUM.doorZ);
  for (let i = 0; i < 200; i++) {
    stepSim(sim, { forward: 0, strafe: -1, sprint: true, yawDelta: 0, pitchDelta: 0 }, 1 / 60, boxes);
  }
  assert.ok(sim.x < -12, `did not enter x=${sim.x}`);
  assert.ok(sim.x > -23.2, `tunneled the back wall x=${sim.x}`);
});

test("south pier stops a strafe from the opening view", () => {
  const sim = createSim(SPAWN.x, SPAWN.z);
  for (let i = 0; i < 180; i++) {
    stepSim(sim, { forward: 0, strafe: -1, sprint: true, yawDelta: 0, pitchDelta: 0 }, 1 / 60, boxes);
  }
  assert.ok(sim.x > -9.3, `walked through atrium face x=${sim.x}`);
  assert.ok(sim.x < -6, `never reached the wall x=${sim.x}`);
});
