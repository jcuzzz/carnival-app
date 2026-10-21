import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { QueryClientProvider } from "@tanstack/react-query";
import App from "./App";
import { clearCacheOnSessionChange, queryClient } from "./api/queryClient";
import { bootstrapEntraSession } from "./auth/entra";
import { initBranding } from "./branding/branding";
import { initHouseMarks } from "./branding/houseMarks";
import { resumeAfterSignIn, syncWorker } from "./api/syncSetup";
import ErrorBoundary from "./shell/ErrorBoundary";
import { initTheme } from "./shell/theme";
import { ensurePersistentStorage } from "./sync/storagePersistence";
import { pruneResolvedOutboxItems } from "./sync/retention";
import "@fontsource-variable/inter";
import "./tokens.css";
import "./styles.css";

// Before the first paint: a device set to outdoor must never flash the default palette.
initTheme();

// Not awaited: blocking first paint on a read, then a fetch, would hold it hostage to the uplink.
void initBranding();

// The houses read needs a session (`/branding` does not), so it keeps fetching until one exists.
void initHouseMarks();

syncWorker.start();

// Never awaited: it touches only resolved rows, so the live queue is unaffected however it ends.
void pruneResolvedOutboxItems();

// Cached reads belong to the official who fetched them.
clearCacheOnSessionChange(queryClient);

void bootstrapEntraSession(() => void resumeAfterSignIn());

// Unsynced results sit in an evictable IndexedDB until granted; rendering must never wait on it.
void ensurePersistentStorage();

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <ErrorBoundary>
      <QueryClientProvider client={queryClient}>
        <App />
      </QueryClientProvider>
    </ErrorBoundary>
  </StrictMode>,
);
