import { useEffect, useRef } from 'react';
import { ambience } from '@/lib/hiddenThemeAudio';
import { createScene } from '@/lib/hiddenThemeScenes';
import { useHiddenThemeStore } from '@/stores/hiddenThemeStore';

/**
 * The set dressing for a hidden theme: an animated canvas behind the app, a screen-wide overlay
 * (scanlines, grain, vignette — styled per theme in index.css) above it, and the ambient music.
 *
 * Renders nothing when no hidden theme is active, so the ordinary portal pays no cost. Mounted by
 * {@link KeyboardLayer} next to the toast.
 */
export function HiddenThemeScene() {
  const theme = useHiddenThemeStore((s) => s.theme);
  const sound = useHiddenThemeStore((s) => s.sound);
  const canvasRef = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!theme || !canvas) return;
    const scene = createScene(theme, canvas);
    scene.start();
    return () => scene.stop();
  }, [theme]);

  useEffect(() => {
    ambience.set(theme, sound);
  }, [theme, sound]);

  // Silence on unmount (sign-out, layout teardown) rather than playing on over the login page.
  useEffect(() => () => ambience.set(null, false), []);

  if (!theme) return null;

  return (
    <>
      <canvas ref={canvasRef} className="hidden-theme-scene" aria-hidden />
      <div className="hidden-theme-overlay" aria-hidden />
    </>
  );
}
