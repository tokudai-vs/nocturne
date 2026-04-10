import { useEffect, useState } from 'react';

export default function PlaybackTransition() {
  const [mounted, setMounted] = useState(false);
  const [opacity, setOpacity] = useState(0);

  useEffect(() => {
    const unsub1 = window.api.player.onStarting(() => {
      setMounted(true);
      // Force a frame before setting opacity so the CSS transition triggers
      requestAnimationFrame(() => {
        requestAnimationFrame(() => {
          setOpacity(1);
        });
      });
    });

    const unsub2 = window.api.player.onExited(() => {
      // Instantly show solid black (no fade in — we need it NOW to cover window show)
      setMounted(true);
      setOpacity(1);
      // Then fade out after main window is painted
      setTimeout(() => {
        setOpacity(0);
        setTimeout(() => setMounted(false), 400);
      }, 300);
    });

    const unsub3 = window.api.player.onStartFailed(() => {
      setOpacity(0);
      setTimeout(() => setMounted(false), 400);
    });

    return () => {
      unsub1();
      unsub2();
      unsub3();
    };
  }, []);

  if (!mounted) return null;

  return (
    <div
      style={{
        position: 'fixed',
        inset: 0,
        zIndex: 9999,
        background: '#000',
        opacity,
        transition: 'opacity 350ms ease-in-out',
        pointerEvents: 'none',
      }}
    />
  );
}
