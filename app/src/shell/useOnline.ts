import { useEffect, useState } from "react";
import { useReachable } from "../api/reachability";

// `navigator.onLine` is not reachability: the pool AP is usually up with the uplink dead.
export function useOnline(): boolean {
  const [online, setOnline] = useState(() =>
    typeof navigator === "undefined" ? true : navigator.onLine,
  );
  useEffect(() => {
    const on = () => setOnline(true);
    const off = () => setOnline(false);
    window.addEventListener("online", on);
    window.addEventListener("offline", off);
    return () => {
      window.removeEventListener("online", on);
      window.removeEventListener("offline", off);
    };
  }, []);
  return online;
}

export type ConnectivityState =
  | "connected"
  | "offline"
  // On a network, but requests are not reaching the server.
  | "unreachable";

// "Offline" wins when both are bad: it is the one an official can act on.
export function useConnectivity(): ConnectivityState {
  const online = useOnline();
  const reachable = useReachable();
  if (!online) return "offline";
  return reachable ? "connected" : "unreachable";
}
