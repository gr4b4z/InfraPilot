import type { HiddenTheme } from '@/stores/hiddenThemeStore';

/**
 * Animated backdrops for the hidden themes — one canvas painter per theme, drawn behind the app.
 *
 * The app's shell makes its outer surface transparent and its page canvas translucent while a
 * hidden theme is active (see `--bg-shell` and the `:root.theme-*` blocks in index.css), so whatever
 * this paints shows through dimly under the content. Everything here is generated at runtime: no
 * image assets, no network.
 *
 * Painters are kept deliberately dim and slow. They are set dressing behind dense operational
 * tables, so text must stay legible over them — every scene's brightest features sit low or at the
 * edges, and the page's translucent surfaces knock them back further.
 *
 * `prefers-reduced-motion` renders a single still frame instead of animating, and the loop pauses
 * while the tab is hidden.
 */
export interface Scene {
  start(): void;
  stop(): void;
}

interface Painter {
  /** Frames per second to aim for. Rain reads better stepped; particles want to be smooth. */
  fps: number;
  /** Called on start and every resize; (re)seeds anything that depends on the dimensions. */
  init(w: number, h: number): void;
  /**
   * Draw one frame. `t` is seconds since start, `dt` seconds since the previous frame — zero for the
   * still frame rendered under reduced motion, which painters use to skip transient effects.
   */
  frame(ctx: CanvasRenderingContext2D, w: number, h: number, t: number, dt: number): void;
}

export function createScene(theme: HiddenTheme, canvas: HTMLCanvasElement): Scene {
  const painter = PAINTERS[theme]();
  const ctx = canvas.getContext('2d');
  const reduced = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;

  let active = false;
  let raf = 0;
  let w = 0;
  let h = 0;
  let startedAt = 0;
  let last = 0;

  const resize = () => {
    if (!ctx) return;
    const dpr = Math.min(window.devicePixelRatio || 1, 1.5);
    w = window.innerWidth;
    h = window.innerHeight;
    canvas.width = Math.round(w * dpr);
    canvas.height = Math.round(h * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    painter.init(w, h);
    if (reduced) painter.frame(ctx, w, h, 0, 0);
  };

  const loop = (now: number) => {
    if (!active || !ctx) return;
    raf = window.requestAnimationFrame(loop);
    const interval = 1000 / painter.fps;
    if (now - last < interval) return;
    const dt = Math.min((now - last) / 1000, 0.1);
    last = now;
    painter.frame(ctx, w, h, (now - startedAt) / 1000, dt);
  };

  const run = () => {
    if (reduced || !active) return;
    window.cancelAnimationFrame(raf);
    last = performance.now();
    raf = window.requestAnimationFrame(loop);
  };

  const onVisibility = () => {
    if (document.hidden) window.cancelAnimationFrame(raf);
    else run();
  };

  return {
    start() {
      if (active || !ctx) return;
      active = true;
      startedAt = performance.now();
      resize();
      window.addEventListener('resize', resize);
      document.addEventListener('visibilitychange', onVisibility);
      run();
    },
    stop() {
      active = false;
      window.cancelAnimationFrame(raf);
      window.removeEventListener('resize', resize);
      document.removeEventListener('visibilitychange', onVisibility);
    },
  };
}

const rand = (min: number, max: number) => min + Math.random() * (max - min);
const pick = <T,>(items: readonly T[]): T => items[Math.floor(Math.random() * items.length)];

// ── Matrix: digital rain ─────────────────────────────────────────────────────────────────────────

const GLYPHS = [...'アイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワン0123456789ABCDEFXZ<>/=+*:;'];
const CELL = 18;

function matrix(): Painter {
  let columns: Array<{ y: number; speed: number }> = [];
  return {
    fps: 18,
    init(w, h) {
      columns = Array.from({ length: Math.ceil(w / CELL) + 1 }, () => ({
        y: rand(-h / CELL, h / CELL),
        speed: rand(0.35, 1.1),
      }));
    },
    frame(ctx, w, h, t, dt) {
      ctx.font = `${CELL - 3}px "JetBrains Mono", ui-monospace, monospace`;
      ctx.textBaseline = 'top';

      if (dt === 0) {
        // Still frame: a sparse field of dim glyphs, no streaks.
        ctx.fillStyle = '#020602';
        ctx.fillRect(0, 0, w, h);
        ctx.fillStyle = 'rgba(0, 255, 65, 0.18)';
        for (let i = 0; i < 220; i++) ctx.fillText(pick(GLYPHS), rand(0, w), rand(0, h));
        return;
      }

      // The trail is the previous frames fading out under a translucent wash of the canvas colour.
      ctx.fillStyle = t < 0.2 ? '#020602' : 'rgba(2, 6, 2, 0.17)';
      ctx.fillRect(0, 0, w, h);

      columns.forEach((col, i) => {
        const x = i * CELL;
        const y = Math.floor(col.y) * CELL;
        // Bright head, then the cell above dims to the streak colour as the head moves on.
        ctx.fillStyle = 'rgba(190, 255, 200, 0.85)';
        ctx.fillText(pick(GLYPHS), x, y);
        ctx.fillStyle = 'rgba(0, 255, 65, 0.5)';
        ctx.fillText(pick(GLYPHS), x, y - CELL);
        col.y += col.speed;
        if (y > h + CELL * 4) {
          col.y = rand(-30, -4);
          col.speed = rand(0.35, 1.1);
        }
      });
    },
  };
}

// ── Punk: zine paper, spray splats, caution stripes, the odd glitch ──────────────────────────────

interface Splat {
  x: number;
  y: number;
  r: number;
  colour: string;
  specks: Array<{ dx: number; dy: number; r: number }>;
}

function punk(): Painter {
  let splats: Splat[] = [];
  let glitchAt = 0;
  return {
    fps: 12,
    init(w, h) {
      const colours = ['194, 0, 95', '0, 0, 0', '194, 0, 95', '0, 51, 204'];
      splats = Array.from({ length: 6 }, (_, i) => {
        const r = rand(50, 130);
        return {
          // Hug the edges so the middle of the page, where the tables are, stays plain paper.
          x: i % 2 === 0 ? rand(-r * 0.3, w * 0.18) : rand(w * 0.82, w + r * 0.3),
          y: rand(0, h),
          r,
          colour: colours[i % colours.length],
          specks: Array.from({ length: 26 }, () => {
            const a = rand(0, Math.PI * 2);
            const d = r * rand(0.9, 1.9);
            return { dx: Math.cos(a) * d, dy: Math.sin(a) * d, r: rand(1, 5) };
          }),
        };
      });
      glitchAt = rand(4, 9);
    },
    frame(ctx, w, h, t, dt) {
      const paper = ctx.createLinearGradient(0, 0, w, h);
      paper.addColorStop(0, '#FFEE2E');
      paper.addColorStop(1, '#F2D800');
      ctx.fillStyle = paper;
      ctx.fillRect(0, 0, w, h);

      for (const s of splats) {
        const g = ctx.createRadialGradient(s.x, s.y, 0, s.x, s.y, s.r);
        g.addColorStop(0, `rgba(${s.colour}, 0.85)`);
        g.addColorStop(0.55, `rgba(${s.colour}, 0.5)`);
        g.addColorStop(1, `rgba(${s.colour}, 0)`);
        ctx.fillStyle = g;
        ctx.fillRect(s.x - s.r, s.y - s.r, s.r * 2, s.r * 2);
        ctx.fillStyle = `rgba(${s.colour}, 0.7)`;
        for (const k of s.specks) {
          ctx.beginPath();
          ctx.arc(s.x + k.dx, s.y + k.dy, k.r, 0, Math.PI * 2);
          ctx.fill();
        }
      }

      // Caution tape across the bottom-right corner.
      ctx.save();
      ctx.translate(w * 0.86, h * 0.9);
      ctx.rotate(-0.42);
      ctx.fillStyle = '#000000';
      ctx.fillRect(-w * 0.5, -22, w, 44);
      ctx.fillStyle = '#C2005F';
      for (let x = -w * 0.5; x < w * 0.5; x += 44) {
        ctx.beginPath();
        ctx.moveTo(x, -22);
        ctx.lineTo(x + 22, -22);
        ctx.lineTo(x, 22);
        ctx.lineTo(x - 22, 22);
        ctx.closePath();
        ctx.fill();
      }
      ctx.restore();

      // A glitch: two frames of torn black bars every several seconds. Never in the still frame.
      if (dt > 0 && t > glitchAt) {
        if (t < glitchAt + 0.17) {
          ctx.fillStyle = 'rgba(0, 0, 0, 0.75)';
          for (let i = 0; i < 3; i++) ctx.fillRect(rand(-40, w * 0.6), rand(0, h), rand(120, w * 0.7), rand(3, 14));
        } else {
          glitchAt = t + rand(5, 11);
        }
      }
    },
  };
}

// ── Cyberpunk: synthwave horizon ─────────────────────────────────────────────────────────────────

function cyberpunk(): Painter {
  let stars: Array<{ x: number; y: number; r: number; p: number }> = [];
  let ridge: number[] = [];
  return {
    fps: 30,
    init(w, h) {
      stars = Array.from({ length: 160 }, () => ({ x: rand(0, w), y: rand(0, h * 0.5), r: rand(0.6, 1.8), p: rand(0, Math.PI * 2) }));
      // Mountain ridge heights, one per 40px; dips in the middle so the sun sits in a valley.
      const n = Math.ceil(w / 40) + 2;
      ridge = Array.from({ length: n }, (_, i) => {
        const centre = Math.abs(i / n - 0.5) * 2; // 0 at centre, 1 at the edges
        return h * (0.03 + centre * 0.13) * rand(0.6, 1.4);
      });
    },
    frame(ctx, w, h, t) {
      const horizon = h * 0.62;
      const cx = w / 2;

      const sky = ctx.createLinearGradient(0, 0, 0, horizon);
      sky.addColorStop(0, '#07021A');
      sky.addColorStop(0.55, '#1B0B45');
      sky.addColorStop(1, '#5A1050');
      ctx.fillStyle = sky;
      ctx.fillRect(0, 0, w, horizon);

      for (const s of stars) {
        const a = 0.25 + 0.75 * (0.5 + 0.5 * Math.sin(t * 1.4 + s.p));
        ctx.fillStyle = `rgba(255, 255, 255, ${a * 0.8})`;
        ctx.fillRect(s.x, s.y, s.r, s.r);
      }

      // Sun: glow, disc, then the classic horizontal cuts thickening toward the bottom.
      const R = Math.min(w, h) * 0.17;
      const cy = horizon - R * 0.15;
      const glow = ctx.createRadialGradient(cx, cy, R * 0.5, cx, cy, R * 2.4);
      glow.addColorStop(0, 'rgba(255, 100, 150, 0.35)');
      glow.addColorStop(1, 'rgba(255, 100, 150, 0)');
      ctx.fillStyle = glow;
      ctx.fillRect(0, 0, w, horizon);

      ctx.save();
      ctx.beginPath();
      ctx.rect(0, 0, w, horizon);
      ctx.clip();
      ctx.beginPath();
      ctx.arc(cx, cy, R, 0, Math.PI * 2);
      const sun = ctx.createLinearGradient(0, cy - R, 0, cy + R);
      sun.addColorStop(0, '#FFE500');
      sun.addColorStop(0.5, '#FF7A3D');
      sun.addColorStop(1, '#FF2A6D');
      ctx.fillStyle = sun;
      ctx.fill();
      ctx.fillStyle = 'rgba(40, 8, 60, 0.92)';
      const drift = (t * 6) % 16;
      for (let i = 0; i < 9; i++) {
        const y = cy + R * 0.02 + i * 7 + i * i * 1.5 + drift;
        if (y < cy + R) ctx.fillRect(cx - R, y, R * 2, 2 + i * 1.4);
      }
      ctx.restore();

      // Distant ridge.
      ctx.fillStyle = '#12062E';
      ctx.beginPath();
      ctx.moveTo(0, horizon);
      ridge.forEach((rh, i) => ctx.lineTo(i * 40, horizon - rh));
      ctx.lineTo(w, horizon);
      ctx.closePath();
      ctx.fill();
      ctx.strokeStyle = 'rgba(255, 42, 109, 0.5)';
      ctx.lineWidth = 1;
      ctx.beginPath();
      ridge.forEach((rh, i) => (i === 0 ? ctx.moveTo(0, horizon - rh) : ctx.lineTo(i * 40, horizon - rh)));
      ctx.stroke();

      // Ground and the grid rolling toward the viewer.
      const ground = ctx.createLinearGradient(0, horizon, 0, h);
      ground.addColorStop(0, '#2A0A4A');
      ground.addColorStop(1, '#0B0520');
      ctx.fillStyle = ground;
      ctx.fillRect(0, horizon, w, h - horizon);

      ctx.lineWidth = 1.2;
      const phase = (t * 0.55) % 1;
      for (let i = 1; i <= 26; i++) {
        const d = i - phase;
        if (d <= 1) continue;
        const y = horizon + (h - horizon) / d;
        const near = (y - horizon) / (h - horizon);
        ctx.strokeStyle = `rgba(255, 42, 109, ${0.12 + 0.55 * near})`;
        ctx.beginPath();
        ctx.moveTo(0, y);
        ctx.lineTo(w, y);
        ctx.stroke();
      }
      ctx.strokeStyle = 'rgba(0, 240, 255, 0.32)';
      const spacing = Math.max(70, w / 12);
      for (let k = -16; k <= 16; k++) {
        const xb = cx + k * spacing * 1.7;
        ctx.beginPath();
        ctx.moveTo(cx + (xb - cx) * 0.01, horizon);
        ctx.lineTo(xb, h);
        ctx.stroke();
      }
      ctx.fillStyle = 'rgba(0, 240, 255, 0.85)';
      ctx.fillRect(0, horizon - 1, w, 2);
    },
  };
}

// ── Forest Fairy: light through the canopy, fireflies ────────────────────────────────────────────

interface Firefly {
  x: number;
  y: number;
  vx: number;
  vy: number;
  r: number;
  p: number;
  rate: number;
  colour: string;
}

function forest(): Painter {
  let flies: Firefly[] = [];
  let shafts: Array<{ x: number; width: number; lean: number; p: number }> = [];
  return {
    fps: 30,
    init(w, h) {
      const colours = ['255, 211, 77', '255, 211, 77', '255, 110, 199', '166, 255, 77'];
      flies = Array.from({ length: Math.round(Math.min(90, (w * h) / 16000)) }, () => ({
        x: rand(0, w),
        y: rand(0, h),
        vx: rand(-10, 10),
        vy: rand(-8, 8),
        r: rand(1.2, 2.8),
        p: rand(0, Math.PI * 2),
        rate: rand(0.6, 1.8),
        colour: pick(colours),
      }));
      shafts = Array.from({ length: 5 }, (_, i) => ({
        x: w * (0.1 + i * 0.2) + rand(-w * 0.05, w * 0.05),
        width: rand(40, 110),
        lean: rand(h * 0.15, h * 0.4),
        p: rand(0, Math.PI * 2),
      }));
    },
    frame(ctx, w, h, t, dt) {
      const bg = ctx.createLinearGradient(0, 0, 0, h);
      bg.addColorStop(0, '#0B2417');
      bg.addColorStop(1, '#05130B');
      ctx.fillStyle = bg;
      ctx.fillRect(0, 0, w, h);

      const canopy = ctx.createRadialGradient(w * 0.55, -h * 0.15, 0, w * 0.55, -h * 0.15, h * 0.95);
      canopy.addColorStop(0, 'rgba(166, 255, 77, 0.14)');
      canopy.addColorStop(1, 'rgba(166, 255, 77, 0)');
      ctx.fillStyle = canopy;
      ctx.fillRect(0, 0, w, h);

      for (const s of shafts) {
        const a = 0.05 + 0.035 * Math.sin(t * 0.25 + s.p);
        const g = ctx.createLinearGradient(0, 0, 0, h);
        g.addColorStop(0, `rgba(255, 240, 180, ${a * 1.6})`);
        g.addColorStop(1, 'rgba(255, 240, 180, 0)');
        ctx.fillStyle = g;
        ctx.beginPath();
        ctx.moveTo(s.x - s.width / 2, 0);
        ctx.lineTo(s.x + s.width / 2, 0);
        ctx.lineTo(s.x + s.lean + s.width, h);
        ctx.lineTo(s.x + s.lean - s.width, h);
        ctx.closePath();
        ctx.fill();
      }

      for (const f of flies) {
        if (dt > 0) {
          f.x += f.vx * dt + Math.sin(t * 0.9 + f.p) * 0.35;
          f.y += f.vy * dt + Math.cos(t * 0.7 + f.p) * 0.3;
          if (f.x < -10) f.x = w + 10;
          if (f.x > w + 10) f.x = -10;
          if (f.y < -10) f.y = h + 10;
          if (f.y > h + 10) f.y = -10;
        }
        const a = 0.15 + 0.85 * Math.pow(0.5 + 0.5 * Math.sin(t * f.rate + f.p), 2);
        const g = ctx.createRadialGradient(f.x, f.y, 0, f.x, f.y, f.r * 5);
        g.addColorStop(0, `rgba(${f.colour}, ${a * 0.55})`);
        g.addColorStop(1, `rgba(${f.colour}, 0)`);
        ctx.fillStyle = g;
        ctx.fillRect(f.x - f.r * 5, f.y - f.r * 5, f.r * 10, f.r * 10);
        ctx.fillStyle = `rgba(${f.colour}, ${a})`;
        ctx.beginPath();
        ctx.arc(f.x, f.y, f.r, 0, Math.PI * 2);
        ctx.fill();
      }
    },
  };
}

const PAINTERS: Record<HiddenTheme, () => Painter> = { matrix, punk, cyberpunk, forest };
