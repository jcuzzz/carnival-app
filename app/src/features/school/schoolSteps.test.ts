import { describe, expect, it } from "vitest";
import type { Carnival, RoleAssignment, SchoolSettings } from "../../api/types";
import { firstUndoneStep } from "../carnivals/setupSteps";
import { UNCONFIGURED_SCHOOL_NAME, schoolSteps, type SchoolFacts } from "./schoolSteps";

const settings = (name: string, setupConfirmed = false): SchoolSettings =>
  ({ name, setupConfirmed, houses: [{ code: "BANKSIA" }, { code: "WATTLE" }] }) as SchoolSettings;

const assignment = (upn: string, extra: Partial<RoleAssignment> = {}): RoleAssignment =>
  ({ upn, bootstrap: false, revokedAtUtc: null, role: "Recorder", ...extra }) as RoleAssignment;

const fresh: SchoolFacts = {
  settings: settings(UNCONFIGURED_SCHOOL_NAME),
  assignments: [
    assignment("backup-admin", { role: "SystemAdministrator", assignedBySubject: "host" }),
  ],
  subject: "local:1",
  carnivals: [],
  localAccounts: true,
};

function step(facts: SchoolFacts, id: string) {
  return schoolSteps(facts).find((s) => s.id === id)!;
}

describe("schoolSteps", () => {
  it("lists the three rows in order, with staff optional", () => {
    const steps = schoolSteps(fresh);
    expect(steps.map((s) => [s.id, s.path, s.optional])).toEqual([
      ["settings", "/school/settings", false],
      ["staff", "/school/people", true],
      ["carnival", "/carnivals", false],
    ]);
    expect(firstUndoneStep(steps)?.id).toBe("settings");
  });

  it("points staff at Roles where the build has no People screen", () => {
    expect(step({ ...fresh, localAccounts: false }, "staff").path).toBe("/school/roles");
  });
});

describe("schoolSteps — school name, colours and houses", () => {
  it("is undone until an administrator saves, even with a name seeded from .env", () => {
    expect(step(fresh, "settings").done).toBe(false);
    const seeded = step({ ...fresh, settings: settings("Riverbend College") }, "settings");
    expect(seeded.done).toBe(false);
    expect(seeded.detail).toBe(
      "The name and colour every screen shows, and the houses students belong to",
    );
  });

  it("is done once School settings have been saved", () => {
    const saved = step({ ...fresh, settings: settings("Riverbend College", true) }, "settings");
    expect(saved.done).toBe(true);
    expect(saved.detail).toBe("Riverbend College · 2 houses");
  });

  it("is unknown when the settings read failed or has not landed", () => {
    expect(step({ ...fresh, settings: null }, "settings").done).toBeNull();
    expect(step({ ...fresh, settings: undefined }, "settings").done).toBeNull();
  });
});

describe("schoolSteps — staff accounts", () => {
  it("is undone while the only role is the first administrator's own", () => {
    expect(step(fresh, "staff").done).toBe(false);
  });

  it("is done once a local administrator grants someone a role", () => {
    const staffed = step(
      {
        ...fresh,
        assignments: [
          ...fresh.assignments!,
          assignment("jsmith", { assignedBySubject: "local:1" }),
        ],
      },
      "staff",
    );
    expect(staffed.done).toBe(true);
    expect(staffed.detail).toBe("1 person given a role");
  });

  it("is done for an Entra administrator, whose subject is an object id no upn matches", () => {
    const oid = "7f1c2a9e-0000-4000-8000-000000000001";
    const facts: SchoolFacts = {
      ...fresh,
      subject: oid,
      localAccounts: false,
      assignments: [
        assignment("it@school.example", { bootstrap: true, id: null, assignedBySubject: null }),
      ],
    };
    expect(step(facts, "staff").done).toBe(false);
    const staffed = step(
      {
        ...facts,
        assignments: [
          ...facts.assignments!,
          assignment("jsmith@school.example", { assignedBySubject: oid }),
        ],
      },
      "staff",
    );
    expect(staffed.done).toBe(true);
  });

  it("does not count a role since revoked", () => {
    const facts = {
      ...fresh,
      assignments: [
        ...fresh.assignments!,
        assignment("jsmith", {
          assignedBySubject: "local:1",
          revokedAtUtc: "2026-11-01T00:00:00Z",
        }),
      ],
    };
    expect(step(facts, "staff").done).toBe(false);
  });

  it("is unknown when the assignments read failed or has not landed", () => {
    expect(step({ ...fresh, assignments: null }, "staff").done).toBeNull();
    expect(step({ ...fresh, assignments: undefined }, "staff").done).toBeNull();
  });
});

describe("schoolSteps — your first carnival", () => {
  it("is undone with no carnival and done with one", () => {
    expect(step(fresh, "carnival").done).toBe(false);
    const one = step({ ...fresh, carnivals: [{ id: "c-1" } as Carnival] }, "carnival");
    expect(one.done).toBe(true);
    expect(one.detail).toBe("1 carnival");
  });

  it("is unknown when the carnival list failed or has not landed", () => {
    expect(step({ ...fresh, carnivals: null }, "carnival").done).toBeNull();
    expect(step({ ...fresh, carnivals: undefined }, "carnival").done).toBeNull();
  });
});
