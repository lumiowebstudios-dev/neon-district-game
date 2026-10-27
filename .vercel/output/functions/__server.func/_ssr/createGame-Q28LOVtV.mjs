import { C as SRGBColorSpace, D as Texture, E as SphereGeometry, O as TorusGeometry, S as RingGeometry, T as ShaderMaterial, _ as Object3D, a as Color, b as PointLight, c as FogExp2, d as InstancedMesh, f as LinearFilter, g as MeshStandardMaterial, h as MeshBasicMaterial, i as CanvasTexture, l as Group, m as Mesh, n as AmbientLight, o as CylinderGeometry, p as LinearMipmapLinearFilter, r as BoxGeometry, s as DirectionalLight, t as WebGLRenderer, u as HemisphereLight, v as PerspectiveCamera, w as Scene, x as RepeatWrapping, y as PlaneGeometry } from "../_libs/three.mjs";
//#region node_modules/.nitro/vite/services/ssr/assets/createGame-Q28LOVtV.js
var ATRIUM = {
	faceX: -9.05,
	backX: -23.6,
	z0: -4,
	z1: 15,
	doorZ0: 3.2,
	doorZ1: 6.1,
	doorZ: 4.65
};
var LIGHTS = [
	{
		color: 4842751,
		intensity: 28,
		distance: 16,
		x: -14.2,
		y: 3.15,
		z: 4.65
	},
	{
		color: 16757082,
		intensity: 16,
		distance: 10,
		x: -17.4,
		y: 1.7,
		z: -.7
	},
	{
		color: 16757082,
		intensity: 36,
		distance: 24,
		x: .4,
		y: 4.4,
		z: -6.5
	},
	{
		color: 12887807,
		intensity: 26,
		distance: 15,
		x: 7.4,
		y: 3.6,
		z: 5.1
	}
];
function locationFor(x, z) {
	if (x < ATRIUM.faceX - .2 && x > ATRIUM.backX + .35 && z > ATRIUM.z0 + .4 && z < ATRIUM.z1 - .4) return "Lumina Atrium";
	if (z < -15.5 && x > -13 && x < 13) return "Spire Court";
	return "The Promenade";
}
function collidersFrom(blocks) {
	const out = [];
	for (const b of blocks) {
		if (!b.collide) continue;
		out.push({
			minX: b.x - b.w / 2,
			maxX: b.x + b.w / 2,
			minY: b.y - b.h / 2,
			maxY: b.y + b.h / 2,
			minZ: b.z - b.d / 2,
			maxZ: b.z + b.d / 2
		});
	}
	return out;
}
function buildDistrict() {
	const blocks = [];
	const add = (x, y, z, w, h, d, mat, collide = true, kind = "box") => {
		blocks.push({
			x,
			y,
			z,
			w,
			h,
			d,
			mat,
			collide,
			kind
		});
	};
	add(0, .02, -8, 6.4, .03, 46, "floor", false);
	add(0, 2.2, 28.2, 52, 4.4, .6, "metal");
	add(0, 2.4, -38, 52, 4.8, .6, "metal");
	add(-26.4, 2.1, -5, .55, 4.2, 68, "metal");
	add(24.2, 2.1, -5, .55, 4.2, 68, "metal");
	const t = .48;
	const ah = 8.8;
	const ay = ah / 2;
	const eastX = ATRIUM.faceX - t / 2;
	const northLen = ATRIUM.doorZ0 - ATRIUM.z0;
	const northZ = (ATRIUM.z0 + ATRIUM.doorZ0) / 2;
	const southLen = ATRIUM.z1 - ATRIUM.doorZ1;
	const southZ = (ATRIUM.doorZ1 + ATRIUM.z1) / 2;
	add(eastX, ay, northZ, t, ah, northLen, "plaster");
	add(eastX, ay, southZ, t, ah, southLen, "plaster");
	const headerH = 5.6000000000000005;
	add(eastX, 6, ATRIUM.doorZ, t, headerH, ATRIUM.doorZ1 - ATRIUM.doorZ0, "plaster");
	const westX = ATRIUM.backX + t / 2;
	const aLen = ATRIUM.z1 - ATRIUM.z0;
	const aZ = (ATRIUM.z0 + ATRIUM.z1) / 2;
	add(westX, ay, aZ, t, ah, aLen, "plaster");
	const innerW = ATRIUM.faceX - t - (ATRIUM.backX + t);
	const innerX = (ATRIUM.faceX - t + ATRIUM.backX + t) / 2;
	add(innerX, ay, ATRIUM.z0 + t / 2, innerW, ah, t, "plaster");
	add(innerX, ay, ATRIUM.z1 - t / 2, innerW, ah, t, "plaster");
	add(ATRIUM.faceX + .05, ay, northZ, .08, ah * .94, northLen * .92, "winCyan", false);
	add(ATRIUM.faceX + .05, ay, southZ, .08, ah * .94, southLen * .92, "winCyan", false);
	add(ATRIUM.faceX + .05, 6, ATRIUM.doorZ, .08, headerH * .9, (ATRIUM.doorZ1 - ATRIUM.doorZ0) * .96, "winCyan", false);
	add(-8.15, 3.32, ATRIUM.doorZ, 1.7, .1, 3.35, "cyan", false);
	add(-8.55, .025, ATRIUM.doorZ, 1.15, .02, 2.7, "cyan", false);
	const ceilY = 4.42;
	add(innerX, .025, aZ, innerW - .3, .03, aLen - 1.1, "floor", false);
	add(innerX, ceilY, aZ, innerW - .15, .14, aLen - .7, "trim", false);
	add(innerX, 4.22, ATRIUM.doorZ, innerW - 1.4, .06, .28, "cyan", false);
	add(-16.8, .28, 3.4, .72, .56, .72, "trim");
	add(-16.8, 1.35, 3.4, .07, 1.7, .07, "cyan", false);
	add(-16.8, 2.15, 3.4, .22, .22, .22, "cyan", false, "sphere");
	add(-19.15, .32, -.85, 1.05, .55, 2.7, "sofa");
	add(-19.15, .66, -.85, .92, .12, 2.45, "cushion", false);
	add(-15.45, .32, -.85, 1.05, .55, 2.7, "sofa");
	add(-15.45, .66, -.85, .92, .12, 2.45, "cushion", false);
	add(-17.3, .26, -.85, 1.25, .4, 1.15, "wood");
	add(-17.3, .5, -.85, .35, .08, .35, "amber", false);
	add(-17.3, .03, -.85, 3.3, .02, 4.1, "rug", false);
	add(-16.2, .52, 11.1, 3.5, 1.04, 1.15, "trim");
	add(-16.2, 1.08, 11.1, 3.55, .06, 1.22, "cyan", false);
	add(-21.7, .7, 12.4, 2.3, 1.4, 3.1, "warm");
	add(-20.2, 2.35, 13.15, 3.6, .12, 1.5, "trim", false);
	add(-11.35, 2.15, .15, .46, 4.3, .46, "trim");
	add(-11.35, 2.15, 9.7, .46, 4.3, .46, "trim");
	add(-12.4, .32, 13.2, .7, .64, .7, "planter", true, "cyl");
	add(-12.4, .95, 13.2, .62, .62, .62, "plant", false, "sphere");
	add(-12.2, .32, -2.4, .7, .64, .7, "planter", true, "cyl");
	add(-12.2, .95, -2.4, .62, .62, .62, "plant", false, "sphere");
	add(ATRIUM.faceX + .08, 4.6, ATRIUM.z0 + .08, .1, 8.2, .1, "cyan", false);
	add(ATRIUM.faceX + .08, 4.6, ATRIUM.z1 - .08, .1, 8.2, .1, "cyan", false);
	add(-17.6, 2.7, -12.2, 17.2, 5.4, 16.4, "metal");
	add(-9.02, 2.7, -12.2, .08, 4.9, 14.8, "winCyan", false);
	add(14.42, 3.3, 6.5, 10.75, 6.6, 17, "metal");
	add(15.1, 9.2, 6.6, 7.4, 5.2, 12.2, "trim", false);
	add(15.55, 14.15, 7.1, 4.4, 4.6, 7.6, "metal", false);
	add(9, 3.15, 6.5, .08, 6.05, 15.6, "winViolet", false);
	add(11.2, 8.6, 6.6, .06, 3.6, 10.4, "winViolet", false);
	add(8.15, 3.22, 5.2, 1.85, .12, 3.5, "violet", false);
	add(9.02, 1.45, 5.2, .06, 2.5, 1.7, "trim", false);
	add(8.95, 5.2, -1.85, .12, 9.2, .12, "violet", false);
	add(16.55, 2.45, -11.5, 15.1, 4.9, 18.6, "metal");
	add(9.02, 2.45, -11.5, .08, 4.4, 16.6, "winViolet", false);
	add(0, 1.5, -27.5, 14.6, 3, 13, "metal");
	add(-10.15, 1.65, -27.5, 6.1, 3.3, 13, "trim");
	add(10.15, 1.65, -27.5, 6.1, 3.3, 13, "trim");
	add(0, 1.55, -20.9, 24.2, 2.7, .08, "winAmber", false);
	add(0, 11.2, -27.5, 4.5, 16.4, 4.5, "metal", false);
	add(0, 11.2, -25.18, 4.15, 15.2, .08, "winAmber", false);
	add(0, 20.6, -27.5, 2.3, 4.8, 2.3, "trim", false);
	add(0, 24.4, -27.5, .16, 6.4, .16, "amber", false);
	add(0, .75, -18.75, 1.2, 1.5, .16, "trim");
	add(0, 3.1, 20.6, 32, 6.2, 11.1, "metal");
	add(0, 3.05, 14.95, 29.5, 5.5, .08, "winCyan", false);
	for (const [lx, lz] of [
		[-6.35, 13.2],
		[6.35, 1.1],
		[-6.35, -6.8],
		[6.35, -14.6]
	]) {
		add(lx, 1.7, lz, .14, 3.4, .14, "trim");
		add(lx, 3.5, lz, .42, .42, .42, "amber", false, "sphere");
	}
	add(5.2, .85, 11.45, .62, 1.7, .42, "trim");
	add(5.2, 1.72, 11.45, .7, .08, .5, "cyan", false);
	add(-4.7, .42, 13.1, 1.5, .42, .55, "wood");
	add(3.6, .42, -2.4, 1.5, .42, .55, "wood");
	add(-5.4, .28, 3.15, .55, .56, .55, "planter", true, "cyl");
	add(-5.4, .78, 3.15, .48, .48, .48, "plant", false, "sphere");
	add(5.5, .28, -8.4, .55, .56, .55, "planter", true, "cyl");
	add(5.5, .78, -8.4, .48, .48, .48, "plant", false, "sphere");
	const gutters = [];
	for (let z = 14.2; z >= -19.2; z -= 2.15) {
		gutters.push({
			x: -6.55,
			z,
			tint: "cyan"
		});
		gutters.push({
			x: 6.55,
			z,
			tint: "violet"
		});
	}
	return {
		blocks,
		spots: [
			{
				id: "guide",
				x: 5.2,
				z: 11.45,
				radius: 1.85,
				prompt: "Read the district guide",
				title: "District guide",
				body: "The promenade is private after dusk. Lumina keeps its doors open. Violet seals the exchange until sunrise. Amber is a landmark, not a door."
			},
			{
				id: "violet",
				x: 7.35,
				z: 5.2,
				radius: 2.05,
				prompt: "Check the exchange doors",
				title: "Violet Exchange",
				body: "Members only tonight. The glass is warm. The lock is not. Come back when the sign stops humming."
			},
			{
				id: "sculpture",
				x: -16.8,
				z: 3.4,
				radius: 1.7,
				prompt: "Inspect the installation",
				title: "Still Water",
				body: "A filament tuned to the street's hum. The concierge used to say it only listens to people who stop walking."
			},
			{
				id: "desk",
				x: -14.3,
				z: 11.1,
				radius: 1.55,
				prompt: "Read the concierge note",
				title: "Lumina desk",
				body: "Doors stay open. Names stay out of the book. If you are cold, the lounge is north of the nave — the amber side."
			},
			{
				id: "spire",
				x: 0,
				z: -18.75,
				radius: 1.9,
				prompt: "Read the spire plaque",
				title: "Amber Spire",
				body: "Raised as a navigation fire for the old air lanes. It still burns so the district can find itself in the fog."
			}
		],
		signs: [
			{
				text: "LUMINA",
				x: -8.72,
				y: 5.55,
				z: ATRIUM.doorZ,
				w: 3.5,
				h: .72,
				rotY: Math.PI / 2,
				color: "#7eebff"
			},
			{
				text: "VIOLET",
				x: 8.72,
				y: 6.15,
				z: 5.2,
				w: 3.6,
				h: .78,
				rotY: -Math.PI / 2,
				color: "#d4c4ff"
			},
			{
				text: "AMBER",
				x: 0,
				y: 6.4,
				z: -24.85,
				w: 3.4,
				h: .86,
				rotY: 0,
				color: "#ffc27a"
			},
			{
				text: "GALLERY",
				x: 0,
				y: 4.15,
				z: 14.72,
				w: 4.2,
				h: .7,
				rotY: Math.PI,
				color: "#9ad8ff"
			},
			{
				text: "GUIDE",
				x: 5.2,
				y: 1.15,
				z: 11.18,
				w: .52,
				h: .7,
				rotY: Math.PI,
				color: "#7eebff"
			},
			{
				text: "STILL WATER",
				x: -23.05,
				y: 2.35,
				z: 1.2,
				w: 2.6,
				h: 1.7,
				rotY: Math.PI / 2,
				color: "#d7fbff"
			}
		],
		gutters
	};
}
function seeded(seed) {
	let n = seed >>> 0;
	return () => {
		n = Math.imul(n, 1664525) + 1013904223 >>> 0;
		return n / 4294967296;
	};
}
function canvasTex(c) {
	const tex = new CanvasTexture(c);
	tex.colorSpace = SRGBColorSpace;
	tex.anisotropy = 4;
	tex.magFilter = LinearFilter;
	tex.minFilter = LinearMipmapLinearFilter;
	tex.needsUpdate = true;
	return tex;
}
function makeWindow(primary, warm, seed) {
	const c = document.createElement("canvas");
	c.width = 128;
	c.height = 256;
	const g = c.getContext("2d");
	if (!g) return canvasTex(c);
	const rand = seeded(seed);
	g.fillStyle = "#090b11";
	g.fillRect(0, 0, 128, 256);
	const cols = 4;
	const rows = 9;
	const pad = 7;
	const cw = 99 / cols;
	const ch = 202 / rows;
	for (let y = 0; y < rows; y++) for (let x = 0; x < cols; x++) {
		const roll = rand();
		const px = pad + x * 29.75;
		const py = pad + y * 27.444444444444443;
		if (roll < .4) {
			g.globalAlpha = 1;
			g.fillStyle = "#141824";
			g.fillRect(px, py, cw, ch);
		} else {
			g.globalAlpha = .42 + rand() * .58;
			g.fillStyle = roll > .84 ? warm : primary;
			g.fillRect(px, py, cw, ch);
			g.globalAlpha = .55;
			g.fillStyle = "#ffffff";
			g.fillRect(px, py, cw, Math.max(1, ch * .07));
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
			x += (rand() - .5) * 48;
			y += (rand() - .5) * 48;
			g.lineTo(x, y);
		}
		g.stroke();
	}
	const tex = canvasTex(c);
	tex.wrapS = RepeatWrapping;
	tex.wrapT = RepeatWrapping;
	tex.repeat.set(10, 10);
	return tex;
}
function paintSign(text, color, art) {
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
		g.globalAlpha = .85;
		g.lineWidth = 2;
		g.beginPath();
		g.moveTo(70, 260);
		g.bezierCurveTo(140, 80, 220, 300, 280, 120);
		g.bezierCurveTo(330, 40, 400, 200, 450, 90);
		g.stroke();
		g.globalAlpha = .35;
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
function geometryFor(b) {
	if (b.kind === "cyl") return new CylinderGeometry(b.w / 2, b.w / 2, b.h, 18);
	if (b.kind === "sphere") return new SphereGeometry(b.w / 2, 16, 12);
	return new BoxGeometry(b.w, b.h, b.d);
}
function mountDistrict(parent) {
	const district = buildDistrict();
	const asphalt = makeAsphalt();
	const winCyan = makeWindow("#7eebff", "#ffc27a", 3);
	const winViolet = makeWindow("#c4b5fd", "#7eebff", 11);
	const winAmber = makeWindow("#ffc27a", "#fff1d6", 29);
	const standard = (color, roughness, metalness, extra = {}) => new MeshStandardMaterial({
		color,
		roughness,
		metalness,
		...extra
	});
	const glow = (color, intensity) => new MeshStandardMaterial({
		color: 460812,
		emissive: color,
		emissiveIntensity: intensity,
		roughness: .32,
		metalness: .05,
		toneMapped: false
	});
	const mats = {
		metal: standard(1513762, .46, .78),
		trim: standard(2303539, .38, .7),
		plaster: standard(2368044, .9, .02),
		warm: standard(2761776, .84, .06),
		sofa: standard(2893366, .78, .08),
		cushion: standard(3946064, .7, .05),
		wood: standard(3812388, .62, .12),
		plant: standard(1324082, .7, .05, {
			emissive: 799536,
			emissiveIntensity: .35
		}),
		planter: standard(1316382, .55, .4),
		rug: standard(1708068, .95, 0),
		floor: standard(1316900, .2, .72),
		cyan: glow(4120831, 1.8),
		violet: glow(12031231, 1.7),
		amber: glow(16757082, 1.85),
		winCyan: standard(16777215, .42, .25, {
			map: winCyan,
			emissive: 16777215,
			emissiveMap: winCyan,
			emissiveIntensity: .72
		}),
		winViolet: standard(16777215, .42, .25, {
			map: winViolet,
			emissive: 16777215,
			emissiveMap: winViolet,
			emissiveIntensity: .7
		}),
		winAmber: standard(16777215, .4, .28, {
			map: winAmber,
			emissive: 16777215,
			emissiveMap: winAmber,
			emissiveIntensity: .78
		})
	};
	const ground = new Mesh(new PlaneGeometry(84, 78), new MeshStandardMaterial({
		map: asphalt,
		color: 16777215,
		roughness: .94,
		metalness: .04
	}));
	ground.rotation.x = -Math.PI / 2;
	ground.position.set(0, 0, -6);
	parent.add(ground);
	const ring = new Mesh(new RingGeometry(1.55, 1.72, 48), glow(4120831, 1.15));
	ring.rotation.x = -Math.PI / 2;
	ring.position.set(0, .02, 7.4);
	parent.add(ring);
	for (const b of district.blocks) {
		if (b.mat === "hidden") continue;
		const mesh = new Mesh(geometryFor(b), mats[b.mat]);
		mesh.position.set(b.x, b.y, b.z);
		parent.add(mesh);
	}
	const cyanGut = district.gutters.filter((g) => g.tint === "cyan");
	const violetGut = district.gutters.filter((g) => g.tint === "violet");
	const gutterGeo = new BoxGeometry(.1, .03, 1.2);
	const placeGutters = (list, mat) => {
		if (!list.length) return;
		const mesh = new InstancedMesh(gutterGeo, mat, list.length);
		const dummy = new Object3D();
		list.forEach((g, i) => {
			dummy.position.set(g.x, .035, g.z);
			dummy.updateMatrix();
			mesh.setMatrixAt(i, dummy.matrix);
		});
		mesh.instanceMatrix.needsUpdate = true;
		parent.add(mesh);
	};
	placeGutters(cyanGut, mats.cyan);
	placeGutters(violetGut, mats.violet);
	const ringMat = glow(16757082, 2.1);
	const torus = new TorusGeometry(2.55, .055, 8, 48);
	for (const y of [
		7.2,
		11.2,
		15.2,
		19.1
	]) {
		const halo = new Mesh(torus, ringMat);
		halo.rotation.x = Math.PI / 2;
		halo.position.set(0, y, -27.5);
		parent.add(halo);
	}
	const signMeshes = [];
	const addSign = (sign) => {
		const art = sign.text === "STILL WATER";
		const canvas = paintSign(sign.text, sign.color, art);
		const tex = canvasTex(canvas);
		signMeshes.push({
			canvas,
			tex,
			art,
			text: sign.text,
			color: sign.color
		});
		const nx = Math.sin(sign.rotY);
		const nz = Math.cos(sign.rotY);
		const glowMat = new MeshBasicMaterial({
			color: new Color(sign.color),
			transparent: true,
			opacity: .42,
			blending: 2,
			depthWrite: false,
			toneMapped: false
		});
		const halo = new Mesh(new PlaneGeometry(sign.w * 1.45, sign.h * 1.9), glowMat);
		halo.position.set(sign.x - nx * .05, sign.y, sign.z - nz * .05);
		halo.rotation.y = sign.rotY;
		parent.add(halo);
		const panel = new Mesh(new PlaneGeometry(sign.w, sign.h), new MeshBasicMaterial({
			map: tex,
			transparent: true,
			depthWrite: true,
			toneMapped: false
		}));
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
	document.fonts?.ready.then(repaint);
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
		}
	};
}
/** First-person sim. Yaw 0 looks down world −Z. A strafes left, D strafes right. */
var RADIUS = .34;
var EYE = 1.64;
var BODY = 1.72;
var WALK = 4.35;
var SPRINT = 7.05;
var PITCH_LIMIT = 1.42;
function basis(yaw) {
	return {
		fx: -Math.sin(yaw),
		fz: -Math.cos(yaw),
		rx: Math.cos(yaw),
		rz: -Math.sin(yaw)
	};
}
function createSim(x, z) {
	return {
		x,
		z,
		yaw: 0,
		pitch: -.08,
		vx: 0,
		vz: 0,
		walk: 0,
		bob: 0
	};
}
function hits(x, z, boxes, radius = RADIUS, body = BODY) {
	for (let i = 0; i < boxes.length; i++) {
		const b = boxes[i];
		if (b.maxY < .4 || b.minY > body) continue;
		if (x + radius > b.minX && x - radius < b.maxX && z + radius > b.minZ && z - radius < b.maxZ) return true;
	}
	return false;
}
function clamp$1(v, a, b) {
	return Math.max(a, Math.min(b, v));
}
function stepSim(sim, input, dt, boxes) {
	const dtC = Math.min(Math.max(dt, 0), .05);
	sim.yaw += input.yawDelta;
	sim.pitch = clamp$1(sim.pitch + input.pitchDelta, -1.42, PITCH_LIMIT);
	const { fx, fz, rx, rz } = basis(sim.yaw);
	let wishX = fx * input.forward + rx * input.strafe;
	let wishZ = fz * input.forward + rz * input.strafe;
	const mag = Math.hypot(wishX, wishZ);
	if (mag > 1) {
		wishX /= mag;
		wishZ /= mag;
	}
	const moving = mag > .08;
	const speed = moving ? input.sprint ? SPRINT : WALK : 0;
	const tx = (mag > 1 ? wishX : wishX) * speed;
	const tz = (mag > 1 ? wishZ : wishZ) * speed;
	const k = 1 - Math.exp(-12 * dtC);
	sim.vx += (tx - sim.vx) * k;
	sim.vz += (tz - sim.vz) * k;
	if (!moving && Math.hypot(sim.vx, sim.vz) < .06) {
		sim.vx = 0;
		sim.vz = 0;
	}
	const dx = sim.vx * dtC;
	const dz = sim.vz * dtC;
	const sub = Math.max(1, Math.ceil(Math.hypot(dx, dz) / .16));
	for (let i = 0; i < sub; i++) {
		const sx = dx / sub;
		const sz = dz / sub;
		if (!hits(sim.x + sx, sim.z, boxes)) sim.x += sx;
		else sim.vx = 0;
		if (!hits(sim.x, sim.z + sz, boxes)) sim.z += sz;
		else sim.vz = 0;
	}
	const planar = Math.hypot(sim.vx, sim.vz);
	if (planar > .4) {
		sim.walk += planar * dtC;
		sim.bob = Math.sin(sim.walk * 2.15);
	} else sim.bob *= Math.exp(-10 * dtC);
}
var GAME_KEYS = /* @__PURE__ */ new Set([
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
	"Space"
]);
function clamp(v, a, b) {
	return Math.max(a, Math.min(b, v));
}
function deadzone(x, y, dz = .18) {
	const m = Math.hypot(x, y);
	if (m < dz) return {
		x: 0,
		y: 0
	};
	const s = (m - dz) / (1 - dz) / m;
	return {
		x: x * s,
		y: y * s
	};
}
function createGame(canvas, hooks) {
	const reduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
	let mobile = window.matchMedia("(pointer: coarse)").matches || window.innerWidth < 860;
	let playing = false;
	let muted = false;
	let touchX = 0;
	let touchY = 0;
	let touchSprint = false;
	let pendingYaw = 0;
	let pendingPitch = 0;
	let dragging = false;
	let wasLocked = false;
	const keys = /* @__PURE__ */ new Set();
	const injected = /* @__PURE__ */ new Set();
	let ePrev = false;
	let padPrev = false;
	const sim = createSim(0, 7.4);
	const renderer = new WebGLRenderer({
		canvas,
		antialias: !mobile,
		alpha: false,
		powerPreference: "high-performance",
		stencil: false
	});
	renderer.outputColorSpace = SRGBColorSpace;
	renderer.toneMapping = 4;
	renderer.toneMappingExposure = 1.02;
	let dpr = Math.min(window.devicePixelRatio || 1, mobile ? 1.15 : 1.5);
	renderer.setPixelRatio(dpr);
	const scene = new Scene();
	scene.background = new Color(723218);
	scene.fog = new FogExp2(723218, .02);
	const fog = scene.fog;
	const fogOut = new Color(723218);
	const fogIn = new Color(1314326);
	const camera = new PerspectiveCamera(76, 1, .08, 420);
	camera.rotation.order = "YXZ";
	const sky = new Mesh(new SphereGeometry(280, 24, 16), new ShaderMaterial({
		side: 1,
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
      `
	}));
	sky.renderOrder = -1;
	sky.frustumCulled = false;
	scene.add(sky);
	scene.add(new HemisphereLight(2760772, 789520, .62));
	scene.add(new AmbientLight(2893888, .28));
	const moon = new DirectionalLight(12964607, .62);
	moon.position.set(-18, 28, 10);
	scene.add(moon);
	const root = new Group();
	scene.add(root);
	const mounted = mountDistrict(root);
	const colliders = mounted.colliders;
	const spots = mounted.district.spots;
	for (const spec of LIGHTS) {
		const light = new PointLight(spec.color, spec.intensity, spec.distance, 2);
		light.position.set(spec.x, spec.y, spec.z);
		scene.add(light);
	}
	const moteGeo = new SphereGeometry(.045, 8, 8);
	const moteColors = [
		8317951,
		16761466,
		13944063
	];
	const motes = [];
	for (let i = 0; i < 9; i++) {
		const mat = new MeshBasicMaterial({
			color: moteColors[i % 3],
			transparent: true,
			opacity: .8,
			blending: 2,
			depthWrite: false,
			toneMapped: false
		});
		const mesh = new Mesh(moteGeo, mat);
		const x = (i % 2 === 0 ? -1 : 1) * (2.2 + i % 3 * .7);
		const z = 12 - i * 3.3;
		mesh.position.set(x, 1.4, z);
		scene.add(mesh);
		motes.push({
			mesh,
			x,
			z,
			phase: i * .8,
			speed: .6 + i % 3 * .15,
			base: 1.15 + i % 4 * .22
		});
	}
	let audio = null;
	let master = null;
	const ensureAudio = () => {
		if (audio) {
			if (audio.state === "suspended") audio.resume();
			return;
		}
		const AC = window.AudioContext;
		if (!AC) return;
		audio = new AC();
		master = audio.createGain();
		master.gain.value = muted ? 0 : .16;
		master.connect(audio.destination);
		const filter = audio.createBiquadFilter();
		filter.type = "lowpass";
		filter.frequency.value = 220;
		filter.connect(master);
		const bed = audio.createGain();
		bed.gain.value = .09;
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
	const blip = (freq) => {
		if (!audio || !master || muted) return;
		const o = audio.createOscillator();
		const g = audio.createGain();
		o.type = "triangle";
		o.frequency.value = freq;
		g.gain.setValueAtTime(.04, audio.currentTime);
		g.gain.exponentialRampToValueAtTime(8e-4, audio.currentTime + .28);
		o.connect(g);
		g.connect(master);
		o.start();
		o.stop(audio.currentTime + .3);
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
	let nearest = null;
	let mode = null;
	const held = (code) => injected.has(code) || playing && keys.has(code);
	const publishHud = (location, prompt, nextMode) => {
		const key = `${location}|${prompt ?? ""}|${nextMode ?? ""}`;
		if (key === hudKey) return;
		hudKey = key;
		hooks.onHud({
			location,
			prompt,
			mode: nextMode
		});
	};
	const setDetail = (spot) => {
		const id = spot?.id ?? "";
		if (id === detailId) return;
		detailId = id;
		hooks.onDetail(spot ? {
			id: spot.id,
			title: spot.title,
			body: spot.body
		} : null);
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
	const frame = (now) => {
		const dt = Math.min((now - last) / 1e3, .05);
		last = now;
		if (dt > .034) slow += 1;
		else slow = Math.max(0, slow - 1);
		if (slow > 40 && dpr > 1) {
			dpr = 1;
			resize();
			slow = -1e3;
		}
		let padX = 0;
		let padY = 0;
		let padYaw = 0;
		let padPitch = 0;
		let padSprint = false;
		let padInteract = false;
		const pads = navigator.getGamepads?.();
		if (pads) for (const pad of pads) {
			if (!pad) continue;
			const left = deadzone(pad.axes[0] ?? 0, pad.axes[1] ?? 0);
			const right = deadzone(pad.axes[2] ?? 0, pad.axes[3] ?? 0, .2);
			padX += left.x;
			padY += -left.y;
			padYaw += -right.x * 1.7 * dt;
			padPitch += -right.y * 1.45 * dt;
			if (pad.buttons[0]?.pressed) padInteract = true;
			if (pad.buttons[7]?.pressed || pad.buttons[5]?.pressed) padSprint = true;
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
		const yawDelta = simulate ? clamp(pendingYaw + padYaw, -.5, .5) : 0;
		const pitchDelta = simulate ? clamp(pendingPitch + padPitch, -.5, .5) : 0;
		pendingYaw = 0;
		pendingPitch = 0;
		if (simulate) stepSim(sim, {
			forward,
			strafe,
			sprint: sprint && mag > .08,
			yawDelta,
			pitchDelta
		}, dt, colliders);
		else {
			sim.vx = 0;
			sim.vz = 0;
			sim.bob *= Math.exp(-10 * dt);
		}
		const { rx, rz } = basis(sim.yaw);
		const bobAmp = reduced ? 0 : 1;
		const bobY = sim.bob * .04 * bobAmp;
		const bobS = Math.cos(sim.walk * 1.05) * .012 * Math.min(1, Math.abs(sim.bob)) * bobAmp;
		camera.position.set(sim.x + rx * bobS, EYE + bobY, sim.z + rz * bobS);
		camera.rotation.y = sim.yaw;
		camera.rotation.x = sim.pitch;
		fov += ((sprint && mag > .2 ? 82 : 76) - fov) * (1 - Math.exp(-6 * dt));
		if (Math.abs(camera.fov - fov) > .04) {
			camera.fov = fov;
			camera.updateProjectionMatrix();
		}
		const inside = locationFor(sim.x, sim.z) === "Lumina Atrium";
		fog.density += ((inside ? .03 : .02) - fog.density) * (1 - Math.exp(-2.4 * dt));
		fog.color.lerp(inside ? fogIn : fogOut, 1 - Math.exp(-2 * dt));
		if (!reduced) {
			const t = now * .001;
			for (const mote of motes) {
				mote.mesh.position.y = mote.base + Math.sin(t * mote.speed + mote.phase) * .32;
				mote.mesh.position.x = mote.x + Math.sin(t * .35 + mote.phase) * .18;
			}
		}
		const location = locationFor(sim.x, sim.z);
		let prompt = null;
		mode = null;
		nearest = null;
		if (detailId) {
			prompt = "Close";
			mode = "read";
		} else {
			const outer = Math.hypot(sim.x - -7.55, sim.z - ATRIUM.doorZ) < 2.35 && sim.x > ATRIUM.faceX;
			const inner = Math.hypot(sim.x - -11.05, sim.z - ATRIUM.doorZ) < 2.15 && sim.x < ATRIUM.faceX - .45;
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
		if (eDown && !ePrev || padInteract && !padPrev) interact();
		ePrev = eDown;
		padPrev = padInteract;
		renderer.render(scene, camera);
	};
	renderer.setAnimationLoop(frame);
	publishHud(locationFor(sim.x, sim.z), null, null);
	const ac = new AbortController();
	const opt = { signal: ac.signal };
	window.addEventListener("keydown", (e) => {
		if (e.code === "Escape" && playing) {
			playing = false;
			keys.clear();
			hooks.onPlaying(false);
			return;
		}
		if (GAME_KEYS.has(e.code)) e.preventDefault();
		keys.add(e.code);
	}, opt);
	window.addEventListener("keyup", (e) => keys.delete(e.code), opt);
	window.addEventListener("blur", () => keys.clear(), opt);
	document.addEventListener("visibilitychange", () => {
		if (document.hidden) keys.clear();
	}, opt);
	window.addEventListener("mousemove", (e) => {
		if (!playing || mobile) return;
		if (!(document.pointerLockElement === canvas) && !dragging) return;
		pendingYaw -= e.movementX * .00215;
		pendingPitch -= e.movementY * .00215;
	}, opt);
	canvas.addEventListener("pointerdown", (e) => {
		if (!playing || mobile || e.button !== 0) return;
		dragging = true;
	}, opt);
	window.addEventListener("pointerup", () => {
		dragging = false;
	}, opt);
	window.addEventListener("pointercancel", () => {
		dragging = false;
	}, opt);
	document.addEventListener("pointerlockchange", () => {
		const locked = document.pointerLockElement === canvas;
		if (wasLocked && !locked && playing && !mobile) {
			playing = false;
			keys.clear();
			hooks.onPlaying(false);
		}
		wasLocked = locked;
	}, opt);
	const onResize = () => resize();
	window.addEventListener("resize", onResize, opt);
	const observer = new ResizeObserver(() => resize());
	observer.observe(canvas);
	const lock = () => {
		if (mobile) return;
		const request = () => {
			try {
				const ret = canvas.requestPointerLock({ unadjustedMovement: true });
				if (ret && typeof ret.catch === "function") ret.catch(() => {
					canvas.requestPointerLock()?.catch(() => void 0);
				});
			} catch {
				try {
					canvas.requestPointerLock();
				} catch {}
			}
		};
		request();
	};
	const probe = {
		getYaw: () => sim.yaw,
		getSpeed: () => Math.hypot(sim.vx, sim.vz),
		getPos: () => ({
			x: sim.x,
			y: 0,
			z: sim.z
		}),
		setKeys: (codes) => {
			injected.clear();
			for (const code of codes) injected.add(code);
		}
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
			pendingYaw -= dx * .0044;
			pendingPitch -= dy * .0044;
		},
		setSprint: (down) => {
			touchSprint = down;
		},
		interact,
		closeDetail: () => setDetail(null),
		setMuted: (next) => {
			muted = next;
			if (master) master.gain.value = next ? 0 : .16;
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
				const mesh = obj;
				if (mesh.geometry) mesh.geometry.dispose();
				const material = mesh.material;
				const list = Array.isArray(material) ? material : material ? [material] : [];
				for (const item of list) {
					const record = item;
					for (const value of Object.values(record)) if (value instanceof Texture) value.dispose();
					item.dispose();
				}
			});
			mounted.dispose();
			renderer.dispose();
			if (audio) audio.close();
		}
	};
}
//#endregion
export { createGame };
