import { useEffect, useRef } from "react";

// Offline, signing out would clear a roster that cannot be re-fetched, so the device is covered
// instead. `aria-modal` is what hides the screen behind from assistive tech.
export default function IdleOverlay({ onResume }: { onResume: () => void }) {
  const button = useRef<HTMLButtonElement | null>(null);
  useEffect(() => button.current?.focus(), []);

  return (
    <div className="idle-overlay" role="dialog" aria-modal="true" aria-label="Screen locked">
      <div className="idle-overlay-panel">
        <strong>Screen locked</strong>
        <p>
          This device has been idle. Nothing has been lost — every capture is still saved here
          and still queued to sync.
        </p>
        <p className="row-sub">
          You are still signed in because this device is offline: signing out now would clear
          the roster it cannot fetch again. It signs out as soon as the network returns.
        </p>
        <button type="button" className="primary" ref={button} onClick={onResume}>
          Tap to resume
        </button>
      </div>
    </div>
  );
}
