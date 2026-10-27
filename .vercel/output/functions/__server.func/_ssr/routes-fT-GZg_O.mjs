import { i as __toESM } from "../_runtime.mjs";
import { K as require_react, b as require_jsx_runtime } from "../_libs/@tanstack/react-router+[...].mjs";
import { i as Pause, n as Volume2, t as VolumeX } from "../_libs/lucide-react.mjs";
//#region node_modules/.nitro/vite/services/ssr/assets/routes-fT-GZg_O.js
var import_react = /* @__PURE__ */ __toESM(require_react());
var import_jsx_runtime = require_jsx_runtime();
function NeonDistrict() {
	const canvasRef = (0, import_react.useRef)(null);
	const gameRef = (0, import_react.useRef)(null);
	const lookRef = (0, import_react.useRef)(null);
	const stickRef = (0, import_react.useRef)(null);
	const [ready, setReady] = (0, import_react.useState)(false);
	const [error, setError] = (0, import_react.useState)(null);
	const [playing, setPlaying] = (0, import_react.useState)(false);
	const [entered, setEntered] = (0, import_react.useState)(false);
	const [mobile, setMobile] = (0, import_react.useState)(false);
	const [muted, setMuted] = (0, import_react.useState)(false);
	const [location, setLocation] = (0, import_react.useState)("The Promenade");
	const [prompt, setPrompt] = (0, import_react.useState)(null);
	const [mode, setMode] = (0, import_react.useState)(null);
	const [detail, setDetail] = (0, import_react.useState)(null);
	const [knob, setKnob] = (0, import_react.useState)({
		x: 0,
		y: 0
	});
	const [hints, setHints] = (0, import_react.useState)(true);
	(0, import_react.useEffect)(() => {
		const canvas = canvasRef.current;
		if (!canvas) return;
		let dead = false;
		let handle = null;
		import("./createGame-Q28LOVtV.mjs").then(({ createGame }) => {
			if (dead) return;
			try {
				handle = createGame(canvas, {
					onHud: (hud) => {
						setLocation(hud.location);
						setPrompt(hud.prompt);
						setMode(hud.mode);
					},
					onDetail: setDetail,
					onPlaying: setPlaying
				});
				if (dead) {
					handle.dispose();
					return;
				}
				const coarse = window.matchMedia("(pointer: coarse)").matches || window.innerWidth < 860;
				handle.setMobile(coarse);
				setMobile(coarse);
				gameRef.current = handle;
				setReady(true);
			} catch (err) {
				setError(err instanceof Error ? err.message : "The 3D view failed to start");
			}
		});
		return () => {
			dead = true;
			handle?.dispose();
			gameRef.current = null;
		};
	}, []);
	(0, import_react.useEffect)(() => {
		const apply = () => {
			const next = window.matchMedia("(pointer: coarse)").matches || window.innerWidth < 860;
			setMobile(next);
			gameRef.current?.setMobile(next);
		};
		const mq = window.matchMedia("(pointer: coarse)");
		mq.addEventListener("change", apply);
		window.addEventListener("resize", apply);
		return () => {
			mq.removeEventListener("change", apply);
			window.removeEventListener("resize", apply);
		};
	}, []);
	(0, import_react.useEffect)(() => {
		if (!playing) return;
		const id = window.setTimeout(() => setHints(false), 7e3);
		return () => window.clearTimeout(id);
	}, [playing]);
	const enter = () => {
		if (!gameRef.current) return;
		setEntered(true);
		setPlaying(true);
		setHints(true);
		gameRef.current.setPlaying(true);
		gameRef.current.lock();
	};
	const pause = () => {
		gameRef.current?.setPlaying(false);
		if (document.pointerLockElement) document.exitPointerLock();
		setPlaying(false);
	};
	return /* @__PURE__ */ (0, import_jsx_runtime.jsxs)("main", {
		className: "game-shell bg-bg font-sans text-fg",
		children: [
			/* @__PURE__ */ (0, import_jsx_runtime.jsx)("canvas", {
				ref: canvasRef,
				className: "game-canvas",
				"aria-label": "Neon District street"
			}),
			/* @__PURE__ */ (0, import_jsx_runtime.jsx)("div", { className: "vignette" }),
			/* @__PURE__ */ (0, import_jsx_runtime.jsx)("div", {
				className: "pointer-events-none absolute inset-0 z-30 flex flex-col justify-between p-4 sm:p-8",
				children: /* @__PURE__ */ (0, import_jsx_runtime.jsxs)("header", {
					className: "flex items-start justify-between gap-3",
					children: [/* @__PURE__ */ (0, import_jsx_runtime.jsx)("p", {
						className: "pointer-events-auto rounded-full border border-line bg-panel/85 px-3 py-2 text-sm text-fg backdrop-blur-md",
						children: /* @__PURE__ */ (0, import_jsx_runtime.jsx)("span", {
							className: "text-cyan",
							children: location
						})
					}), /* @__PURE__ */ (0, import_jsx_runtime.jsxs)("div", {
						className: "pointer-events-auto flex gap-2",
						children: [playing ? /* @__PURE__ */ (0, import_jsx_runtime.jsx)("button", {
							type: "button",
							className: "icon-btn",
							onClick: pause,
							"aria-label": "Pause",
							children: /* @__PURE__ */ (0, import_jsx_runtime.jsx)(Pause, { size: 18 })
						}) : null, /* @__PURE__ */ (0, import_jsx_runtime.jsx)("button", {
							type: "button",
							className: "icon-btn",
							"aria-label": muted ? "Unmute" : "Mute",
							"aria-pressed": muted,
							onClick: () => {
								const next = !muted;
								setMuted(next);
								gameRef.current?.setMuted(next);
							},
							children: muted ? /* @__PURE__ */ (0, import_jsx_runtime.jsx)(VolumeX, { size: 18 }) : /* @__PURE__ */ (0, import_jsx_runtime.jsx)(Volume2, { size: 18 })
						})]
					})]
				})
			}),
			playing ? /* @__PURE__ */ (0, import_jsx_runtime.jsx)("div", { className: "crosshair" }) : null,
			playing && prompt ? /* @__PURE__ */ (0, import_jsx_runtime.jsx)("div", {
				className: "pointer-events-none absolute top-1/2 left-1/2 z-30 mt-8 -translate-x-1/2",
				children: /* @__PURE__ */ (0, import_jsx_runtime.jsxs)("p", {
					className: "flex items-center gap-2 rounded-full border border-line bg-panel/90 px-3 py-2 text-sm text-fg backdrop-blur-md",
					children: [mode === "read" && !mobile ? /* @__PURE__ */ (0, import_jsx_runtime.jsx)("kbd", {
						className: "kbd",
						children: "E"
					}) : null, prompt]
				})
			}) : null,
			detail ? /* @__PURE__ */ (0, import_jsx_runtime.jsxs)("section", {
				className: "panel absolute right-4 bottom-28 left-4 z-40 sm:right-auto sm:bottom-24 sm:left-8 sm:w-96",
				children: [/* @__PURE__ */ (0, import_jsx_runtime.jsxs)("div", {
					className: "mb-3 flex items-start justify-between gap-3",
					children: [/* @__PURE__ */ (0, import_jsx_runtime.jsx)("h2", {
						className: "font-display text-xl font-bold text-fg",
						children: detail.title
					}), /* @__PURE__ */ (0, import_jsx_runtime.jsx)("button", {
						type: "button",
						className: "icon-btn",
						"aria-label": "Close note",
						onClick: () => gameRef.current?.closeDetail(),
						children: "Close"
					})]
				}), /* @__PURE__ */ (0, import_jsx_runtime.jsx)("p", {
					className: "text-sm leading-relaxed text-muted",
					children: detail.body
				})]
			}) : null,
			!playing ? /* @__PURE__ */ (0, import_jsx_runtime.jsx)("div", {
				className: "absolute inset-0 z-40 flex items-end p-4 sm:items-center sm:p-10",
				children: /* @__PURE__ */ (0, import_jsx_runtime.jsxs)("section", {
					className: "panel w-full max-w-md",
					children: [
						/* @__PURE__ */ (0, import_jsx_runtime.jsx)("p", {
							className: "eyebrow text-cyan",
							children: "Night promenade"
						}),
						/* @__PURE__ */ (0, import_jsx_runtime.jsx)("h1", {
							className: "font-display mt-2 text-4xl font-extrabold tracking-tight text-fg sm:text-5xl",
							children: entered ? "Paused" : "Neon District"
						}),
						/* @__PURE__ */ (0, import_jsx_runtime.jsx)("p", {
							className: "mt-3 text-sm leading-relaxed text-muted",
							children: error ? error : entered ? "The street is still there. Resume to walk, or drag to look if the cursor stays free." : "A glass canyon after rain. Walk the promenade, look up at Amber, and step through the open doors of Lumina."
						}),
						/* @__PURE__ */ (0, import_jsx_runtime.jsx)("button", {
							type: "button",
							className: "enter-btn",
							onClick: enter,
							disabled: !ready || Boolean(error),
							children: error ? "Unavailable" : !ready ? "Lighting the street" : entered ? "Resume" : "Enter the street"
						}),
						/* @__PURE__ */ (0, import_jsx_runtime.jsx)("p", {
							className: "mt-3 text-xs leading-relaxed text-muted",
							children: mobile ? "Left stick moves. Drag the right side to look. Sprint and Interact sit on the right." : "WASD moves. Drag or click to look. Shift sprints. E reads signs and notes."
						})
					]
				})
			}) : null,
			playing && hints && !mobile ? /* @__PURE__ */ (0, import_jsx_runtime.jsx)("p", {
				className: "pointer-events-none absolute bottom-6 left-1/2 z-30 hidden -translate-x-1/2 text-xs text-muted sm:block",
				children: "Drag or mouse-look · WASD · Shift sprint · E interact"
			}) : null,
			playing && mobile ? /* @__PURE__ */ (0, import_jsx_runtime.jsxs)(import_jsx_runtime.Fragment, { children: [
				/* @__PURE__ */ (0, import_jsx_runtime.jsx)("div", {
					className: "look-zone absolute inset-0 z-20",
					onPointerDown: (e) => {
						lookRef.current = {
							id: e.pointerId,
							x: e.clientX,
							y: e.clientY
						};
						e.currentTarget.setPointerCapture(e.pointerId);
					},
					onPointerMove: (e) => {
						const look = lookRef.current;
						if (!look || look.id !== e.pointerId) return;
						gameRef.current?.addLook(e.clientX - look.x, e.clientY - look.y);
						look.x = e.clientX;
						look.y = e.clientY;
					},
					onPointerUp: (e) => {
						if (lookRef.current?.id === e.pointerId) lookRef.current = null;
					},
					onPointerCancel: () => {
						lookRef.current = null;
					}
				}),
				/* @__PURE__ */ (0, import_jsx_runtime.jsx)("div", {
					className: "stick absolute bottom-5 left-4 z-30",
					onPointerDown: (e) => {
						e.stopPropagation();
						stickRef.current = {
							id: e.pointerId,
							x: e.clientX,
							y: e.clientY
						};
						e.currentTarget.setPointerCapture(e.pointerId);
					},
					onPointerMove: (e) => {
						const stick = stickRef.current;
						if (!stick || stick.id !== e.pointerId) return;
						const max = 46;
						const cx = Math.max(-46, Math.min(max, e.clientX - stick.x));
						const cy = Math.max(-46, Math.min(max, e.clientY - stick.y));
						setKnob({
							x: cx,
							y: cy
						});
						gameRef.current?.setMove(cx / max, -cy / max);
					},
					onPointerUp: (e) => {
						if (stickRef.current?.id !== e.pointerId) return;
						stickRef.current = null;
						setKnob({
							x: 0,
							y: 0
						});
						gameRef.current?.setMove(0, 0);
					},
					onPointerCancel: () => {
						stickRef.current = null;
						setKnob({
							x: 0,
							y: 0
						});
						gameRef.current?.setMove(0, 0);
					},
					children: /* @__PURE__ */ (0, import_jsx_runtime.jsx)("span", {
						className: "knob",
						style: { transform: `translate(${knob.x}px, ${knob.y}px)` }
					})
				}),
				/* @__PURE__ */ (0, import_jsx_runtime.jsxs)("div", {
					className: "absolute right-4 bottom-5 z-30 flex flex-col gap-3",
					children: [/* @__PURE__ */ (0, import_jsx_runtime.jsx)("button", {
						type: "button",
						className: "touch-btn",
						onPointerDown: (e) => {
							e.stopPropagation();
							e.currentTarget.setPointerCapture(e.pointerId);
							gameRef.current?.setSprint(true);
						},
						onPointerUp: () => gameRef.current?.setSprint(false),
						onPointerCancel: () => gameRef.current?.setSprint(false),
						children: "Sprint"
					}), /* @__PURE__ */ (0, import_jsx_runtime.jsx)("button", {
						type: "button",
						className: mode === "read" ? "touch-btn touch-btn-hot" : "touch-btn",
						onPointerDown: (e) => e.stopPropagation(),
						onClick: () => gameRef.current?.interact(),
						children: "Interact"
					})]
				})
			] }) : null
		]
	});
}
function Home() {
	return /* @__PURE__ */ (0, import_jsx_runtime.jsx)(NeonDistrict, {});
}
//#endregion
export { Home as component };
