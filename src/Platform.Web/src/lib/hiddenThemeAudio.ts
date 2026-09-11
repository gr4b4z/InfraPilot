import type { HiddenTheme } from '@/stores/hiddenThemeStore';

/**
 * Generative ambient music for the hidden themes, synthesised live with the Web Audio API.
 *
 * Nothing is downloaded: every sound is oscillators, filtered noise and a convolver fed with a
 * generated impulse. Each theme has a "voice" — a small scheduler that keeps composing while it
 * runs — and {@link ambience} owns the single AudioContext, fading voices in and out so switching
 * themes or toggling sound never clicks.
 *
 * Browsers only let audio start from a user gesture. The `t s` chord is one, so the normal path
 * just works; if the context still comes up suspended (a persisted "sound on" at page load, say),
 * the next key press or click resumes it.
 */
interface Voice {
  stop(): void;
}

type VoiceFactory = (ctx: AudioContext, out: AudioNode) => Voice;

class Ambience {
  private ctx: AudioContext | null = null;
  private master: GainNode | null = null;
  private current: { theme: HiddenTheme; voice: Voice; bus: GainNode } | null = null;
  private disarmUnlock: (() => void) | null = null;

  /** Reconcile with the store: play `theme` when `on`, otherwise fade whatever is playing out. */
  set(theme: HiddenTheme | null, on: boolean): void {
    if (!on || !theme) {
      this.stopCurrent();
      return;
    }
    if (this.current?.theme === theme) {
      this.resume();
      return;
    }
    this.stopCurrent();
    const ctx = this.ensureContext();
    const bus = ctx.createGain();
    bus.gain.value = 0;
    bus.connect(this.master!);
    const voice = VOICES[theme](ctx, bus);
    bus.gain.linearRampToValueAtTime(1, ctx.currentTime + 2.5);
    this.current = { theme, voice, bus };
    this.resume();
  }

  private ensureContext(): AudioContext {
    if (!this.ctx) {
      this.ctx = new AudioContext();
      this.master = this.ctx.createGain();
      this.master.gain.value = 0.22;
      this.master.connect(this.ctx.destination);
    }
    return this.ctx;
  }

  private resume(): void {
    const ctx = this.ctx;
    if (!ctx || ctx.state === 'running') return;
    ctx
      .resume()
      .then(() => {
        if (ctx.state !== 'running') this.armUnlock();
      })
      .catch(() => this.armUnlock());
  }

  /** Autoplay was refused: resume on the next gesture instead. */
  private armUnlock(): void {
    if (this.disarmUnlock) return;
    const onGesture = () => {
      this.disarmUnlock?.();
      void this.ctx?.resume();
    };
    document.addEventListener('keydown', onGesture);
    document.addEventListener('pointerdown', onGesture);
    this.disarmUnlock = () => {
      document.removeEventListener('keydown', onGesture);
      document.removeEventListener('pointerdown', onGesture);
      this.disarmUnlock = null;
    };
  }

  private stopCurrent(): void {
    const playing = this.current;
    const ctx = this.ctx;
    if (!playing || !ctx) return;
    this.current = null;
    const now = ctx.currentTime;
    playing.bus.gain.cancelScheduledValues(now);
    playing.bus.gain.setValueAtTime(playing.bus.gain.value, now);
    playing.bus.gain.linearRampToValueAtTime(0, now + 1.2);
    window.setTimeout(() => {
      playing.voice.stop();
      playing.bus.disconnect();
    }, 1400);
  }
}

export const ambience = new Ambience();

// ── Building blocks ──────────────────────────────────────────────────────────────────────────────

const rand = (min: number, max: number) => min + Math.random() * (max - min);
const pick = <T,>(items: readonly T[]): T => items[Math.floor(Math.random() * items.length)];

/** A reverb made from exponentially decaying noise — a hall for bells, a room for a drone. */
function reverb(ctx: AudioContext, seconds: number, decay: number): ConvolverNode {
  const rate = ctx.sampleRate;
  const length = Math.floor(rate * seconds);
  const impulse = ctx.createBuffer(2, length, rate);
  for (let ch = 0; ch < 2; ch++) {
    const data = impulse.getChannelData(ch);
    for (let i = 0; i < length; i++) data[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / length, decay);
  }
  const node = ctx.createConvolver();
  node.buffer = impulse;
  return node;
}

function noiseBuffer(ctx: AudioContext, seconds = 2): AudioBuffer {
  const buffer = ctx.createBuffer(1, Math.floor(ctx.sampleRate * seconds), ctx.sampleRate);
  const data = buffer.getChannelData(0);
  for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;
  return buffer;
}

/**
 * Look-ahead scheduler: every 90 ms, books every step that falls within the next 350 ms. Sample
 * accurate timing without a tight loop; the browser can throttle the interval and nothing skips.
 */
function scheduler(ctx: AudioContext, step: number, onStep: (time: number, index: number) => void): () => void {
  let next = ctx.currentTime + 0.1;
  let index = 0;
  const id = window.setInterval(() => {
    while (next < ctx.currentTime + 0.35) {
      onStep(next, index++);
      next += step;
    }
  }, 90);
  return () => window.clearInterval(id);
}

interface ToneOptions {
  type: OscillatorType;
  freq: number;
  time: number;
  attack: number;
  hold: number;
  release: number;
  gain: number;
  detune?: number;
}

/** One enveloped oscillator note; stops and frees itself when the release ends. */
function tone(ctx: AudioContext, dest: AudioNode, o: ToneOptions): void {
  const osc = ctx.createOscillator();
  osc.type = o.type;
  osc.frequency.value = o.freq;
  if (o.detune) osc.detune.value = o.detune;
  const env = ctx.createGain();
  env.gain.setValueAtTime(0.0001, o.time);
  env.gain.exponentialRampToValueAtTime(o.gain, o.time + o.attack);
  env.gain.setValueAtTime(o.gain, o.time + o.attack + o.hold);
  env.gain.exponentialRampToValueAtTime(0.0001, o.time + o.attack + o.hold + o.release);
  osc.connect(env).connect(dest);
  osc.start(o.time);
  osc.stop(o.time + o.attack + o.hold + o.release + 0.05);
}

interface NoiseHitOptions {
  time: number;
  duration: number;
  filter: BiquadFilterType;
  freq: number;
  q?: number;
  gain: number;
}

/** A burst of filtered noise — hats, snares, a gust. */
function noiseHit(ctx: AudioContext, dest: AudioNode, buffer: AudioBuffer, o: NoiseHitOptions): void {
  const src = ctx.createBufferSource();
  src.buffer = buffer;
  const filter = ctx.createBiquadFilter();
  filter.type = o.filter;
  filter.frequency.value = o.freq;
  filter.Q.value = o.q ?? 1;
  const env = ctx.createGain();
  env.gain.setValueAtTime(o.gain, o.time);
  env.gain.exponentialRampToValueAtTime(0.0001, o.time + o.duration);
  src.connect(filter).connect(env).connect(dest);
  src.start(o.time);
  src.stop(o.time + o.duration + 0.05);
}

/** A slow oscillator wired to modulate an AudioParam around its current value. */
function lfo(ctx: AudioContext, param: AudioParam, rate: number, depth: number): OscillatorNode {
  const osc = ctx.createOscillator();
  osc.frequency.value = rate;
  const amount = ctx.createGain();
  amount.gain.value = depth;
  osc.connect(amount).connect(param);
  osc.start();
  return osc;
}

// ── Voices ───────────────────────────────────────────────────────────────────────────────────────

/** Matrix: a low machine-room drone, a slow pulse, and terminal blips ticking away in the dark. */
function matrix(ctx: AudioContext, out: AudioNode): Voice {
  const hall = reverb(ctx, 3, 3);
  hall.connect(out);
  const dry = ctx.createGain();
  dry.gain.value = 0.6;
  dry.connect(out);

  const lowpass = ctx.createBiquadFilter();
  lowpass.type = 'lowpass';
  lowpass.frequency.value = 380;
  lowpass.connect(dry);
  lowpass.connect(hall);

  const drone = ctx.createGain();
  drone.gain.value = 0.3;
  drone.connect(lowpass);
  const oscillators = [
    ['sine', 55],
    ['triangle', 110.6],
    ['sine', 82.4],
  ].map(([type, freq]) => {
    const osc = ctx.createOscillator();
    osc.type = type as OscillatorType;
    osc.frequency.value = freq as number;
    osc.connect(drone);
    osc.start();
    return osc;
  });
  oscillators.push(lfo(ctx, lowpass.frequency, 0.08, 180));

  const blips = [880, 1174.7, 1568, 2093, 2637];
  const stop = scheduler(ctx, 0.25, (time, i) => {
    if (Math.random() < 0.16) {
      tone(ctx, hall, { type: 'square', freq: pick(blips), time, attack: 0.004, hold: 0.015, release: 0.12, gain: 0.03 });
    }
    if (i % 16 === 0) {
      tone(ctx, lowpass, { type: 'sine', freq: 55, time, attack: 0.02, hold: 0.1, release: 0.6, gain: 0.5 });
    }
  });

  return {
    stop() {
      stop();
      oscillators.forEach((o) => o.stop());
      hall.disconnect();
      dry.disconnect();
    },
  };
}

/** Punk: a distorted power-chord riff at 165 BPM with kick, snare and hats. Lo-fi on purpose. */
function punk(ctx: AudioContext, out: AudioNode): Voice {
  const eighth = 60 / 165 / 2;
  const noise = noiseBuffer(ctx);

  const shaper = ctx.createWaveShaper();
  const curve = new Float32Array(1024);
  for (let i = 0; i < curve.length; i++) {
    const x = (i / (curve.length - 1)) * 2 - 1;
    curve[i] = Math.tanh(x * 30);
  }
  shaper.curve = curve;
  const amp = ctx.createBiquadFilter();
  amp.type = 'lowpass';
  amp.frequency.value = 2600;
  const guitar = ctx.createGain();
  guitar.gain.value = 0.1;
  shaper.connect(amp).connect(guitar).connect(out);

  const drums = ctx.createGain();
  drums.gain.value = 0.5;
  drums.connect(out);

  // Roots of the four bars: E2, A2, D3, G2 — every punk song you have ever heard.
  const bars = [82.41, 110, 146.83, 98];
  const stop = scheduler(ctx, eighth, (time, i) => {
    const bar = Math.floor(i / 8) % 4;
    const beat = i % 8;
    const root = bars[bar];
    // Rest on the last eighth of every other bar so the riff breathes.
    if (!(bar % 2 === 1 && beat === 7)) {
      const accent = beat % 2 === 0 ? 0.55 : 0.35;
      for (const ratio of [1, 1.5, 2]) {
        tone(ctx, shaper, { type: 'sawtooth', freq: root * ratio, time, attack: 0.004, hold: eighth * 0.55, release: 0.04, gain: accent, detune: rand(-6, 6) });
      }
    }
    if (beat === 0 || beat === 4) {
      const kick = ctx.createOscillator();
      kick.frequency.setValueAtTime(150, time);
      kick.frequency.exponentialRampToValueAtTime(45, time + 0.12);
      const env = ctx.createGain();
      env.gain.setValueAtTime(0.9, time);
      env.gain.exponentialRampToValueAtTime(0.0001, time + 0.18);
      kick.connect(env).connect(drums);
      kick.start(time);
      kick.stop(time + 0.2);
    }
    if (beat === 2 || beat === 6) {
      noiseHit(ctx, drums, noise, { time, duration: 0.13, filter: 'bandpass', freq: 1800, q: 0.7, gain: 0.5 });
      tone(ctx, drums, { type: 'triangle', freq: 190, time, attack: 0.002, hold: 0.01, release: 0.06, gain: 0.3 });
    }
    noiseHit(ctx, drums, noise, { time, duration: beat % 2 === 1 ? 0.06 : 0.03, filter: 'highpass', freq: 8000, gain: 0.12 });
  });

  return {
    stop() {
      stop();
      guitar.disconnect();
      drums.disconnect();
    },
  };
}

/** Cyberpunk: detuned saw pads over Am–F–C–G, a pulsing bass, soft kick and a wandering arpeggio. */
function cyberpunk(ctx: AudioContext, out: AudioNode): Voice {
  const beat = 0.6; // 100 BPM
  const noise = noiseBuffer(ctx);

  const room = reverb(ctx, 2.5, 2.5);
  room.connect(out);
  const dry = ctx.createGain();
  dry.gain.value = 0.7;
  dry.connect(out);

  const padFilter = ctx.createBiquadFilter();
  padFilter.type = 'lowpass';
  padFilter.frequency.value = 900;
  padFilter.connect(dry);
  padFilter.connect(room);
  const sweep = lfo(ctx, padFilter.frequency, 0.13, 450);

  const bassFilter = ctx.createBiquadFilter();
  bassFilter.type = 'lowpass';
  bassFilter.frequency.value = 220;
  bassFilter.connect(dry);

  // A2 C3 E3 / F2 A2 C3 / C3 E3 G3 / G2 B2 D3
  const chords = [
    [110, 130.81, 164.81],
    [87.31, 110, 130.81],
    [130.81, 164.81, 196],
    [98, 123.47, 146.83],
  ];
  const stop = scheduler(ctx, beat / 2, (time, i) => {
    const eighth = i % 8;
    const chord = chords[Math.floor(i / 8) % chords.length];
    if (eighth === 0) {
      for (const freq of chord) {
        for (const detune of [-8, 8]) {
          tone(ctx, padFilter, { type: 'sawtooth', freq, time, attack: 0.6, hold: 2.4, release: 1.4, gain: 0.045, detune });
        }
      }
    }
    // Bass on every eighth, ducking under the kick on the beat.
    tone(ctx, bassFilter, { type: 'sawtooth', freq: chord[0] / 2, time: time + 0.01, attack: 0.01, hold: 0.14, release: 0.1, gain: eighth % 2 === 0 ? 0.25 : 0.4 });
    if (eighth % 2 === 0) {
      const kick = ctx.createOscillator();
      kick.frequency.setValueAtTime(120, time);
      kick.frequency.exponentialRampToValueAtTime(40, time + 0.14);
      const env = ctx.createGain();
      env.gain.setValueAtTime(0.55, time);
      env.gain.exponentialRampToValueAtTime(0.0001, time + 0.22);
      kick.connect(env).connect(dry);
      kick.start(time);
      kick.stop(time + 0.25);
    } else {
      noiseHit(ctx, dry, noise, { time, duration: 0.05, filter: 'highpass', freq: 9000, gain: 0.05 });
    }
    if (Math.random() < 0.55) {
      tone(ctx, room, { type: 'triangle', freq: pick(chord) * pick([2, 4]), time, attack: 0.01, hold: 0.05, release: 0.35, gain: 0.05 });
    }
  });

  return {
    stop() {
      stop();
      sweep.stop();
      room.disconnect();
      dry.disconnect();
    },
  };
}

/** Forest Fairy: wind through leaves, pentatonic bells drifting left and right, a faint low hum. */
function forest(ctx: AudioContext, out: AudioNode): Voice {
  const hall = reverb(ctx, 4, 2);
  hall.connect(out);
  const dry = ctx.createGain();
  dry.gain.value = 0.3;
  dry.connect(out);

  const wind = ctx.createBufferSource();
  wind.buffer = noiseBuffer(ctx, 3);
  wind.loop = true;
  const leaves = ctx.createBiquadFilter();
  leaves.type = 'bandpass';
  leaves.frequency.value = 550;
  leaves.Q.value = 0.6;
  const gust = ctx.createGain();
  gust.gain.value = 0.11;
  wind.connect(leaves).connect(gust).connect(out);
  wind.start();
  const modulators = [lfo(ctx, leaves.frequency, 0.07, 300), lfo(ctx, gust.gain, 0.11, 0.06)];

  // C major pentatonic, two octaves.
  const bells = [523.25, 587.33, 659.25, 783.99, 880, 1046.5, 1174.66, 1318.5, 1567.98];
  const stop = scheduler(ctx, 0.3, (time, i) => {
    if (Math.random() < 0.26) {
      const freq = pick(bells);
      const pan = ctx.createStereoPanner();
      pan.pan.value = rand(-0.8, 0.8);
      pan.connect(hall);
      pan.connect(dry);
      tone(ctx, pan, { type: 'sine', freq, time, attack: 0.01, hold: 0, release: 2.4, gain: 0.11 });
      tone(ctx, pan, { type: 'triangle', freq: freq * 2, time, attack: 0.01, hold: 0, release: 1.1, gain: 0.025 });
    }
    if (i % 40 === 0) {
      for (const freq of [130.81, 196]) {
        tone(ctx, hall, { type: 'sine', freq, time, attack: 3, hold: 4, release: 4, gain: 0.06 });
      }
    }
  });

  return {
    stop() {
      stop();
      wind.stop();
      modulators.forEach((m) => m.stop());
      hall.disconnect();
      dry.disconnect();
      gust.disconnect();
    },
  };
}

const VOICES: Record<HiddenTheme, VoiceFactory> = { matrix, punk, cyberpunk, forest };
