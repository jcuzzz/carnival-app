import type { PlatformRole, RoleAssignment } from "../../api/types";

/** Every role, in the order a screen lists them, with the words a person reads. */
export const ROLES: readonly { value: PlatformRole; label: string; note: string }[] = [
  {
    value: "SystemAdministrator",
    label: "System administrator",
    note: "School settings, roster imports and roles, plus everything a carnival administrator can do.",
  },
  {
    value: "CarnivalAdministrator",
    label: "Carnival administrator",
    note: "Runs a carnival: events, entries, officials, races, results and finalisation.",
  },
  { value: "Recorder", label: "Recorder", note: "Marshals and records on the deck." },
  { value: "Viewer", label: "Viewer", note: "Read-only." },
];

export function roleLabel(role: PlatformRole): string {
  return ROLES.find((r) => r.value === role)?.label ?? role;
}

/** A session's roles for display, highest first, or "no roles". */
export function roleListLabel(roles: readonly PlatformRole[]): string {
  const labels = ROLES.filter((r) => roles.includes(r.value)).map((r) => r.label);
  const unknown = roles.filter((role) => !ROLES.some((r) => r.value === role));
  return [...labels, ...unknown].join(", ") || "no roles";
}

/** The roles a local account holds in the app's own list: active rows granted to its username. */
export function activeRolesOf(
  username: string,
  assignments: readonly RoleAssignment[],
): PlatformRole[] {
  const name = username.trim().toLowerCase();
  const held = assignments
    .filter((a) => a.revokedAtUtc === null && a.upn.trim().toLowerCase() === name)
    .map((a) => a.role);
  return ROLES.map((r) => r.value).filter((role) => held.includes(role));
}
