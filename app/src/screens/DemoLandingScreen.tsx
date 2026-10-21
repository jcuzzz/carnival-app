import { useState } from "react";
import { resumeAfterSignIn } from "../api/syncSetup";
import { startDemo } from "../auth/demo";
import { navigate } from "../router";

// ADR-0061 §5: no silent re-provision — an expired visitor chooses to start again.
export default function DemoLandingScreen({ expired }: { expired: boolean }) {
  const [starting, setStarting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function start() {
    setStarting(true);
    setError(null);
    try {
      await startDemo();
      void resumeAfterSignIn();
      navigate("/", { replace: true });
    } catch (err) {
      setError(err instanceof Error ? err.message : "The demo could not start. Try again.");
      setStarting(false);
    }
  }

  return (
    <>
      <h1>Try the carnival results platform</h1>
      <p>
        Run a school sports carnival from setup to the final standings: build the program, capture
        results lane by lane on a phone or tablet, and watch house points and records update live.
      </p>

      {expired && (
        <div className="alert notice" role="status">
          Your previous demo has ended. Start a new one below.
        </div>
      )}

      <section className="card" aria-labelledby="demo-sandbox-heading">
        <h2 id="demo-sandbox-heading" className="flush-top">
          Your own sandbox
        </h2>
        <p>
          You get your own copy of Example Synthetic College with a swimming carnival part-way
          through the day. Nobody else can see or change it.
        </p>
        <ul>
          <li>Enter results on the deck and see the dashboard move.</li>
          <li>Set up events, divisions and the program.</li>
          <li>Break a record and see it land in the record book.</li>
        </ul>
        <p className="row-sub">
          Every student is synthetic, and roster imports are switched off. Your copy is deleted after
          two hours without use, or 24 hours after you start.
        </p>
        {error && (
          <div className="alert error" role="alert">
            {error}
          </div>
        )}
        <div className="button-row">
          <button type="button" className="primary" disabled={starting} onClick={() => void start()}>
            {starting ? "Starting…" : "Start demo"}
          </button>
        </div>
      </section>
    </>
  );
}
