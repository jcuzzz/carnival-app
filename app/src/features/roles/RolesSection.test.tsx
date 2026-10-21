// @vitest-environment jsdom
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../../api/client";
import { _resetReachabilityForTests } from "../../api/reachability";
import type { RoleAssignment } from "../../api/types";
import { _resetToastsForTests, getToasts } from "../../shell/toast";
import { renderWithQuery } from "../../test/queryWrapper";

/**
 * ADR-0048 §8. The screen shows who holds what and offers the two writes; the
 * server owns every rule, so a refusal here is a server sentence rendered, not
 * a client gate.
 */

vi.mock("../../api/resources", () => ({
  listRoleAssignments: vi.fn(),
  grantRole: vi.fn(),
  revokeRole: vi.fn(),
}));

import { grantRole, listRoleAssignments, revokeRole } from "../../api/resources";
import RolesSection from "./RolesSection";

function assignment(overrides: Partial<RoleAssignment> = {}): RoleAssignment {
  return {
    id: "11111111-1111-7111-8111-111111111111",
    upn: "coach@school.test",
    displayName: null,
    role: "Recorder",
    bootstrap: false,
    boundToSubject: false,
    reason: "Timing desk for the swimming carnival.",
    assignedBySubject: "sys-1",
    assignedAtUtc: "2026-09-01T02:03:04.000Z",
    revokedAtUtc: null,
    revokedBySubject: null,
    revocationReason: null,
    ...overrides,
  };
}

const BOOTSTRAP = assignment({
  id: null,
  upn: "principal@school.test",
  role: "SystemAdministrator",
  bootstrap: true,
  boundToSubject: true,
  reason: null,
  assignedBySubject: null,
  assignedAtUtc: null,
});

function fill(label: string | RegExp, value: string): void {
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
}

beforeEach(() => {
  cleanup();
  _resetToastsForTests();
  _resetReachabilityForTests();
  vi.mocked(listRoleAssignments).mockReset().mockResolvedValue([]);
  vi.mocked(grantRole).mockReset();
  vi.mocked(revokeRole).mockReset();
});

afterEach(() => {
  cleanup();
  _resetToastsForTests();
});

describe("RolesSection", () => {
  it("says the list is empty without implying nobody has a role", async () => {
    renderWithQuery(<RolesSection offline={false} />);
    // The group-derived roles are invisible here and must not read as "none".
    expect(await screen.findByText(/Roles from the school's sign-in groups still apply/)).toBeTruthy();
  });

  it("lists a grant with its role, who assigned it and why", async () => {
    vi.mocked(listRoleAssignments).mockResolvedValue([assignment()]);
    renderWithQuery(<RolesSection offline={false} />);

    expect(await screen.findByText("coach@school.test · Recorder")).toBeTruthy();
    expect(screen.getByText(/Assigned by sys-1/)).toBeTruthy();
    expect(screen.getByText("Timing desk for the swimming carnival.")).toBeTruthy();
  });

  it("marks an unbound row 'has not signed in yet'", async () => {
    vi.mocked(listRoleAssignments).mockResolvedValue([assignment()]);
    renderWithQuery(<RolesSection offline={false} />);
    expect(await screen.findByText("has not signed in yet")).toBeTruthy();
  });

  it("drops the note once the row is bound, and prefers the display name", async () => {
    vi.mocked(listRoleAssignments).mockResolvedValue([
      assignment({ boundToSubject: true, displayName: "Dana Coach" }),
    ]);
    renderWithQuery(<RolesSection offline={false} />);

    expect(await screen.findByText("Dana Coach · Recorder")).toBeTruthy();
    expect(screen.getByText("coach@school.test")).toBeTruthy();
    expect(screen.queryByText("has not signed in yet")).toBeNull();
  });

  it("shows a bootstrap administrator read-only, with no revoke", async () => {
    vi.mocked(listRoleAssignments).mockResolvedValue([BOOTSTRAP, assignment()]);
    renderWithQuery(<RolesSection offline={false} />);

    expect(await screen.findByText("principal@school.test · System administrator")).toBeTruthy();
    expect(screen.getByText("from configuration")).toBeTruthy();
    expect(screen.getByText(/From the server configuration/)).toBeTruthy();
    // Exactly one revoke trigger — the stored row's.
    expect(screen.getAllByRole("button", { name: /^Revoke / })).toHaveLength(1);
  });

  it("shows a revoked row with its reason, and offers no second revoke", async () => {
    vi.mocked(listRoleAssignments).mockResolvedValue([
      assignment({
        revokedAtUtc: "2026-09-03T01:00:00.000Z",
        revokedBySubject: "sys-2",
        revocationReason: "Left the school.",
      }),
    ]);
    renderWithQuery(<RolesSection offline={false} />);

    expect(await screen.findByText(/Revoked .* by sys-2: Left the school\./)).toBeTruthy();
    expect(screen.getByText("revoked")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /^Revoke / })).toBeNull();
  });

  it("assigns by UPN with a role and a reason", async () => {
    vi.mocked(grantRole).mockResolvedValue(assignment());
    renderWithQuery(<RolesSection offline={false} />);
    await screen.findByRole("heading", { name: "Assign a role" });

    fill("Sign-in address (UPN or email)", "  Coach@School.test  ");
    fireEvent.change(screen.getByLabelText("Role"), { target: { value: "CarnivalAdministrator" } });
    fill(/^Reason \(recorded/, "  Runs the athletics carnival.  ");
    fireEvent.click(screen.getByRole("button", { name: "Assign role" }));

    await waitFor(() =>
      expect(vi.mocked(grantRole).mock.calls[0]?.[0]).toEqual({
        upn: "Coach@School.test",
        role: "CarnivalAdministrator",
        reason: "Runs the athletics carnival.",
      }),
    );
    await waitFor(() => expect(getToasts().map((t) => t.message).join()).toContain("assigned"));
  });

  it("clears the form after a successful assignment so the next one starts blank", async () => {
    vi.mocked(grantRole).mockResolvedValue(assignment());
    renderWithQuery(<RolesSection offline={false} />);
    await screen.findByRole("heading", { name: "Assign a role" });

    fill("Sign-in address (UPN or email)", "coach@school.test");
    fill(/^Reason \(recorded/, "Timing desk.");
    fireEvent.click(screen.getByRole("button", { name: "Assign role" }));

    await waitFor(() =>
      expect(
        (screen.getByLabelText("Sign-in address (UPN or email)") as HTMLInputElement).value,
      ).toBe(""),
    );
  });

  it("cannot assign until both the address and the reason are typed", async () => {
    renderWithQuery(<RolesSection offline={false} />);
    const assign = (await screen.findByRole("button", {
      name: "Assign role",
    })) as HTMLButtonElement;
    expect(assign.disabled).toBe(true);

    fill("Sign-in address (UPN or email)", "coach@school.test");
    expect(assign.disabled).toBe(true);
    fill(/^Reason \(recorded/, "Timing desk.");
    expect(assign.disabled).toBe(false);
  });

  it("shows the server's refusal rather than pre-judging the address", async () => {
    // Validation is server-side (ADR-0048 §7); the form relays what it says.
    vi.mocked(grantRole).mockRejectedValue(
      new ApiError(422, { error: "ROLE_UPN_INVALID", detail: "That is not a sign-in address." }, "/x"),
    );
    renderWithQuery(<RolesSection offline={false} />);
    await screen.findByRole("heading", { name: "Assign a role" });

    fill("Sign-in address (UPN or email)", "not-an-address");
    fill(/^Reason \(recorded/, "Timing desk.");
    fireEvent.click(screen.getByRole("button", { name: "Assign role" }));

    expect(await screen.findByText("That is not a sign-in address.")).toBeTruthy();
  });

  it("relays a duplicate refusal", async () => {
    vi.mocked(grantRole).mockRejectedValue(
      new ApiError(
        409,
        { error: "ROLE_ASSIGNMENT_DUPLICATE", detail: "coach@school.test already holds Recorder." },
        "/x",
      ),
    );
    renderWithQuery(<RolesSection offline={false} />);
    await screen.findByRole("heading", { name: "Assign a role" });

    fill("Sign-in address (UPN or email)", "coach@school.test");
    fill(/^Reason \(recorded/, "Again.");
    fireEvent.click(screen.getByRole("button", { name: "Assign role" }));

    expect(await screen.findByText("coach@school.test already holds Recorder.")).toBeTruthy();
  });

  it("names the person and the role in the revoke confirmation", async () => {
    vi.mocked(listRoleAssignments).mockResolvedValue([
      assignment({ displayName: "Dana Coach", boundToSubject: true }),
    ]);
    renderWithQuery(<RolesSection offline={false} />);

    fireEvent.click(await screen.findByRole("button", { name: "Revoke Recorder for Dana Coach" }));
    expect(screen.getByText(/Revoke Recorder for Dana Coach\?/)).toBeTruthy();
    expect(
      (
        screen.getByRole("button", {
          name: "Confirm revoking Recorder for Dana Coach",
        }) as HTMLButtonElement
      ).disabled,
    ).toBe(true);
  });

  it("requires a typed reason before the revoke can fire", async () => {
    vi.mocked(listRoleAssignments).mockResolvedValue([assignment()]);
    vi.mocked(revokeRole).mockResolvedValue(assignment({ revokedAtUtc: "2026-09-03T01:00:00.000Z" }));
    renderWithQuery(<RolesSection offline={false} />);

    fireEvent.click(
      await screen.findByRole("button", { name: "Revoke Recorder for coach@school.test" }),
    );
    const confirm = screen.getByRole("button", {
      name: "Confirm revoking Recorder for coach@school.test",
    }) as HTMLButtonElement;

    fireEvent.change(screen.getByLabelText(/^Reason for revoking/), {
      target: { value: "   " },
    });
    expect(confirm.disabled).toBe(true);

    fireEvent.change(screen.getByLabelText(/^Reason for revoking/), {
      target: { value: "Left the school." },
    });
    expect(confirm.disabled).toBe(false);
    fireEvent.click(confirm);

    await waitFor(() =>
      expect(vi.mocked(revokeRole).mock.calls[0]).toEqual([
        "11111111-1111-7111-8111-111111111111",
        "Left the school.",
      ]),
    );
  });

  it("relays a self-revoke refusal and leaves the strip open", async () => {
    vi.mocked(listRoleAssignments).mockResolvedValue([
      assignment({ role: "SystemAdministrator", boundToSubject: true }),
    ]);
    vi.mocked(revokeRole).mockRejectedValue(
      new ApiError(
        409,
        { error: "ROLE_SELF_REVOKE", detail: "You cannot revoke your own system administrator role." },
        "/x",
      ),
    );
    renderWithQuery(<RolesSection offline={false} />);

    fireEvent.click(
      await screen.findByRole("button", {
        name: "Revoke System administrator for coach@school.test",
      }),
    );
    fireEvent.change(screen.getByLabelText(/^Reason for revoking/), {
      target: { value: "Stepping back." },
    });
    fireEvent.click(
      screen.getByRole("button", { name: "Confirm revoking System administrator for coach@school.test" }),
    );

    expect(
      await screen.findByText("You cannot revoke your own system administrator role."),
    ).toBeTruthy();
    expect(screen.getByLabelText(/^Reason for revoking/)).toBeTruthy();
  });

  it("disables both writes offline — this screen keeps no offline copy", async () => {
    vi.mocked(listRoleAssignments).mockResolvedValue([assignment()]);
    renderWithQuery(<RolesSection offline={true} />);

    expect(
      (
        (await screen.findByRole("button", {
          name: "Revoke Recorder for coach@school.test",
        })) as HTMLButtonElement
      ).disabled,
    ).toBe(true);
    expect((screen.getByRole("button", { name: "Assign role" }) as HTMLButtonElement).disabled).toBe(
      true,
    );
  });

  it("says the roles could not be read when the list fails", async () => {
    vi.mocked(listRoleAssignments).mockRejectedValue(new TypeError("Failed to fetch"));
    renderWithQuery(<RolesSection offline={false} />);
    expect(await screen.findByText(/the roles couldn’t be read/)).toBeTruthy();
  });
});
