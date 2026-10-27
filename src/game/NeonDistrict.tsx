import { Pause, Volume2, VolumeX } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import type { Detail, GameHandle, Hud } from "./createGame";

export function NeonDistrict() {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const gameRef = useRef<GameHandle | null>(null);
  const lookRef = useRef<{ id: number; x: number; y: number } | null>(null);
  const stickRef = useRef<{ id: number; x: number; y: number } | null>(null);
  const [ready, setReady] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [playing, setPlaying] = useState(false);
  const [entered, setEntered] = useState(false);
  const [mobile, setMobile] = useState(false);
  const [muted, setMuted] = useState(false);
  const [location, setLocation] = useState("The Promenade");
  const [prompt, setPrompt] = useState<string | null>(null);
  const [mode, setMode] = useState<Hud["mode"]>(null);
  const [detail, setDetail] = useState<Detail | null>(null);
  const [knob, setKnob] = useState({ x: 0, y: 0 });
  const [hints, setHints] = useState(true);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    let dead = false;
    let handle: GameHandle | null = null;
    void import("./createGame").then(({ createGame }) => {
      if (dead) return;
      try {
        handle = createGame(canvas, {
          onHud: (hud) => {
            setLocation(hud.location);
            setPrompt(hud.prompt);
            setMode(hud.mode);
          },
          onDetail: setDetail,
          onPlaying: setPlaying,
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

  useEffect(() => {
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

  useEffect(() => {
    if (!playing) return;
    const id = window.setTimeout(() => setHints(false), 7000);
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

  return (
    <main className="game-shell bg-bg font-sans text-fg">
      <canvas ref={canvasRef} className="game-canvas" aria-label="Neon District street" />
      <div className="vignette" />

      <div className="pointer-events-none absolute inset-0 z-30 flex flex-col justify-between p-4 sm:p-8">
        <header className="flex items-start justify-between gap-3">
          <p className="pointer-events-auto rounded-full border border-line bg-panel/85 px-3 py-2 text-sm text-fg backdrop-blur-md">
            <span className="text-cyan">{location}</span>
          </p>
          <div className="pointer-events-auto flex gap-2">
            {playing ? (
              <button type="button" className="icon-btn" onClick={pause} aria-label="Pause">
                <Pause size={18} />
              </button>
            ) : null}
            <button
              type="button"
              className="icon-btn"
              aria-label={muted ? "Unmute" : "Mute"}
              aria-pressed={muted}
              onClick={() => {
                const next = !muted;
                setMuted(next);
                gameRef.current?.setMuted(next);
              }}
            >
              {muted ? <VolumeX size={18} /> : <Volume2 size={18} />}
            </button>
          </div>
        </header>
      </div>

      {playing ? <div className="crosshair" /> : null}

      {playing && prompt ? (
        <div className="pointer-events-none absolute top-1/2 left-1/2 z-30 mt-8 -translate-x-1/2">
          <p className="flex items-center gap-2 rounded-full border border-line bg-panel/90 px-3 py-2 text-sm text-fg backdrop-blur-md">
            {mode === "read" && !mobile ? <kbd className="kbd">E</kbd> : null}
            {prompt}
          </p>
        </div>
      ) : null}

      {detail ? (
        <section className="panel absolute right-4 bottom-28 left-4 z-40 sm:right-auto sm:bottom-24 sm:left-8 sm:w-96">
          <div className="mb-3 flex items-start justify-between gap-3">
            <h2 className="font-display text-xl font-bold text-fg">{detail.title}</h2>
            <button type="button" className="icon-btn" aria-label="Close note" onClick={() => gameRef.current?.closeDetail()}>
              Close
            </button>
          </div>
          <p className="text-sm leading-relaxed text-muted">{detail.body}</p>
        </section>
      ) : null}

      {!playing ? (
        <div className="absolute inset-0 z-40 flex items-end p-4 sm:items-center sm:p-10">
          <section className="panel w-full max-w-md">
            <p className="eyebrow text-cyan">Night promenade</p>
            <h1 className="font-display mt-2 text-4xl font-extrabold tracking-tight text-fg sm:text-5xl">
              {entered ? "Paused" : "Neon District"}
            </h1>
            <p className="mt-3 text-sm leading-relaxed text-muted">
              {error
                ? error
                : entered
                  ? "The street is still there. Resume to walk, or drag to look if the cursor stays free."
                  : "A glass canyon after rain. Walk the promenade, look up at Amber, and step through the open doors of Lumina."}
            </p>
            <button type="button" className="enter-btn" onClick={enter} disabled={!ready || Boolean(error)}>
              {error ? "Unavailable" : !ready ? "Lighting the street" : entered ? "Resume" : "Enter the street"}
            </button>
            <p className="mt-3 text-xs leading-relaxed text-muted">
              {mobile
                ? "Left stick moves. Drag the right side to look. Sprint and Interact sit on the right."
                : "WASD moves. Drag or click to look. Shift sprints. E reads signs and notes."}
            </p>
          </section>
        </div>
      ) : null}

      {playing && hints && !mobile ? (
        <p className="pointer-events-none absolute bottom-6 left-1/2 z-30 hidden -translate-x-1/2 text-xs text-muted sm:block">
          Drag or mouse-look · WASD · Shift sprint · E interact
        </p>
      ) : null}

      {playing && mobile ? (
        <>
          <div
            className="look-zone absolute inset-0 z-20"
            onPointerDown={(e) => {
              lookRef.current = { id: e.pointerId, x: e.clientX, y: e.clientY };
              e.currentTarget.setPointerCapture(e.pointerId);
            }}
            onPointerMove={(e) => {
              const look = lookRef.current;
              if (!look || look.id !== e.pointerId) return;
              gameRef.current?.addLook(e.clientX - look.x, e.clientY - look.y);
              look.x = e.clientX;
              look.y = e.clientY;
            }}
            onPointerUp={(e) => {
              if (lookRef.current?.id === e.pointerId) lookRef.current = null;
            }}
            onPointerCancel={() => {
              lookRef.current = null;
            }}
          />
          <div
            className="stick absolute bottom-5 left-4 z-30"
            onPointerDown={(e) => {
              e.stopPropagation();
              stickRef.current = { id: e.pointerId, x: e.clientX, y: e.clientY };
              e.currentTarget.setPointerCapture(e.pointerId);
            }}
            onPointerMove={(e) => {
              const stick = stickRef.current;
              if (!stick || stick.id !== e.pointerId) return;
              const max = 46;
              const cx = Math.max(-max, Math.min(max, e.clientX - stick.x));
              const cy = Math.max(-max, Math.min(max, e.clientY - stick.y));
              setKnob({ x: cx, y: cy });
              gameRef.current?.setMove(cx / max, -cy / max);
            }}
            onPointerUp={(e) => {
              if (stickRef.current?.id !== e.pointerId) return;
              stickRef.current = null;
              setKnob({ x: 0, y: 0 });
              gameRef.current?.setMove(0, 0);
            }}
            onPointerCancel={() => {
              stickRef.current = null;
              setKnob({ x: 0, y: 0 });
              gameRef.current?.setMove(0, 0);
            }}
          >
            <span className="knob" style={{ transform: `translate(${knob.x}px, ${knob.y}px)` }} />
          </div>
          <div className="absolute right-4 bottom-5 z-30 flex flex-col gap-3">
            <button
              type="button"
              className="touch-btn"
              onPointerDown={(e) => {
                e.stopPropagation();
                e.currentTarget.setPointerCapture(e.pointerId);
                gameRef.current?.setSprint(true);
              }}
              onPointerUp={() => gameRef.current?.setSprint(false)}
              onPointerCancel={() => gameRef.current?.setSprint(false)}
            >
              Sprint
            </button>
            <button
              type="button"
              className={mode === "read" ? "touch-btn touch-btn-hot" : "touch-btn"}
              onPointerDown={(e) => e.stopPropagation()}
              onClick={() => gameRef.current?.interact()}
            >
              Interact
            </button>
          </div>
        </>
      ) : null}
    </main>
  );
}
