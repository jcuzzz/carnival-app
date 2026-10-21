// @vitest-environment jsdom
import { act, cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../api/client";
import { keys } from "../api/queryKeys";
import { _resetReachabilityForTests } from "../api/reachability";
import type { SchoolSettings } from "../api/types";
import { signIn, signOut } from "../auth/session";
import { _resetToastsForTests, getToasts } from "../shell/toast";
import { renderWithQuery } from "../test/queryWrapper";

/**
 * ADR-0045 §6. The screen previews the server's rules but never enforces them:
 * every refusal below arrives as a 422/409 and has to land back on the control
 * the administrator has to fix.
 */

vi.mock("../api/resources", () => ({
  getSchoolSettings: vi.fn(),
  putSchoolSettings: vi.fn(),
  getBranding: vi.fn(),
  getHouseMarks: vi.fn(),
  // The Roles section (ADR-0048) reads on the same screen; its own suite covers it.
  listRoleAssignments: vi.fn(async () => []),
  grantRole: vi.fn(),
  revokeRole: vi.fn(),
  // The Event templates section (ADR-0050) reads here too; its own suite covers it.
  listEventTemplates: vi.fn(async () => []),
  previewTemplateReconcile: vi.fn(),
  applyTemplateReconcile: vi.fn(),
}));

vi.mock("../branding/branding", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../branding/branding")>()),
  refreshBranding: vi.fn(async () => {}),
}));

vi.mock("../branding/houseMarks", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../branding/houseMarks")>()),
  refreshHouseMarks: vi.fn(async () => {}),
}));

import { getSchoolSettings, putSchoolSettings } from "../api/resources";
import { refreshBranding } from "../branding/branding";
import { refreshHouseMarks } from "../branding/houseMarks";
import SchoolSettingsScreen from "./SchoolSettingsScreen";

const STAMP = "2026-09-01T02:03:04.000Z";

function settings(overrides: Partial<SchoolSettings> = {}): SchoolSettings {
  return {
    name: "Riverbend Secondary College",
    brandColour: "#123456",
    houses: [
      { code: "WATTLE", name: "Waratah", colour: "#b42318", inUse: false },
      { code: "MOUNTAIN ASH", name: "Mountain Ash", colour: "#1a7f37", inUse: false },
    ],
    updatedAtUtc: STAMP,
    ...overrides,
  };
}

/** Opens a colour control's panel (ADR-0046) and hands back its hex entry. */
function openColour(name: string): HTMLInputElement {
  const trigger = screen.getByRole("button", { name: new RegExp(`^${name}\\b`) });
  if (trigger.getAttribute("aria-expanded") !== "true") fireEvent.click(trigger);
  return screen.getByLabelText(`${name} hex`) as HTMLInputElement;
}

/** The `<li>` per house, in the order the form renders them. */
function houseRows(): HTMLElement[] {
  return [...document.querySelectorAll<HTMLElement>(".house-rows > li")];
}

beforeEach(() => {
  cleanup();
  _resetToastsForTests();
  _resetReachabilityForTests();
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
  signIn({ subject: "sys-1", displayName: "Alex Admin", roles: ["SystemAdministrator"] });
  vi.mocked(getSchoolSettings).mockReset().mockResolvedValue(settings());
  vi.mocked(putSchoolSettings).mockReset();
  vi.mocked(refreshBranding).mockClear();
  vi.mocked(refreshHouseMarks).mockClear();
});

afterEach(() => {
  cleanup();
  signOut();
  _resetToastsForTests();
});

describe("SchoolSettingsScreen", () => {
  it("loads the school's identity into one form", async () => {
    renderWithQuery(<SchoolSettingsScreen />);

    expect(await screen.findByRole("heading", { name: "School settings", level: 1 })).toBeTruthy();
    const name = (await screen.findByLabelText("School name")) as HTMLInputElement;
    expect(name.value).toBe("Riverbend Secondary College");
    expect(name.maxLength).toBe(200);
    expect(houseRows()).toHaveLength(2);
    expect((screen.getByLabelText("House 2 code") as HTMLInputElement).value).toBe("MOUNTAIN ASH");
    // Nothing typed yet: neither Save nor Cancel has anything to do.
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("is name, brand and houses only — roles and templates moved (ADR-0055 §1)", async () => {
    renderWithQuery(<SchoolSettingsScreen />);

    await screen.findByLabelText("School name");
    expect(screen.getByRole("heading", { name: "Houses", level: 2 })).toBeTruthy();
    expect(screen.queryByRole("heading", { name: "Assign a role" })).toBeNull();
    expect(screen.queryByLabelText("Template")).toBeNull();
    expect(screen.getByRole("link", { name: "← School" }).getAttribute("href")).toBe("#/school");
  });

  it("edits the brand colour inside one panel, and reads out the ink", async () => {
    renderWithQuery(<SchoolSettingsScreen />);

    const trigger = await screen.findByRole("button", { name: /^Brand colour/ });
    expect(trigger.textContent).toContain("#123456");
    expect(screen.getByText(/^white text · \d+\.\d:1$/)).toBeTruthy();
    // Closed, the hex entry does not exist: it lives inside the panel now.
    expect(screen.queryByLabelText("Brand colour hex")).toBeNull();

    const hex = openColour("Brand colour");
    fireEvent.change(hex, { target: { value: "#ffdd00" } });
    expect(trigger.textContent).toContain("#ffdd00");
    expect(screen.getByText(/^dark text · \d+\.\d:1$/)).toBeTruthy();

    // The #rgb shorthand the server also accepts survives the round trip.
    fireEvent.change(hex, { target: { value: "#fd0" } });
    expect(hex.value).toBe("#fd0");
    expect(trigger.textContent).toContain("#fd0");

    // "No colour" is a button now — with the hex field gone from the form, there
    // is no field left to be empty (ADR-0046 §3).
    fireEvent.click(screen.getByRole("button", { name: "No brand colour — default palette" }));
    expect(screen.getByText(/built-in default/)).toBeTruthy();
    expect(screen.queryByLabelText("Brand colour hex")).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });

  it("closes the colour panel on Escape and hands focus back to its trigger", async () => {
    renderWithQuery(<SchoolSettingsScreen />);

    const trigger = await screen.findByRole("button", { name: /^Brand colour/ });
    const hex = openColour("Brand colour");
    expect(trigger.getAttribute("aria-expanded")).toBe("true");

    fireEvent.keyDown(hex, { key: "Escape" });
    expect(screen.queryByLabelText("Brand colour hex")).toBeNull();
    expect(trigger.getAttribute("aria-expanded")).toBe("false");
    expect(document.activeElement).toBe(trigger);
  });

  it("takes a preset colour and closes the panel on the one tap", async () => {
    renderWithQuery(<SchoolSettingsScreen />);

    const trigger = await screen.findByRole("button", { name: /^Brand colour/ });
    openColour("Brand colour");
    fireEvent.click(screen.getByRole("button", { name: "Preset blue #1976d2" }));

    expect(trigger.textContent).toContain("#1976d2");
    expect(screen.queryByLabelText("Brand colour hex")).toBeNull();
    expect(trigger.getAttribute("aria-expanded")).toBe("false");
    expect(document.activeElement).toBe(trigger);
  });

  it("closes the colour panel on an outside pointer, keeping the value and the focus", async () => {
    renderWithQuery(<SchoolSettingsScreen />);

    const trigger = await screen.findByRole("button", { name: /^Brand colour/ });
    openColour("Brand colour");
    const name = screen.getByLabelText("School name");
    name.focus();

    fireEvent.pointerDown(name);
    fireEvent.click(name);
    expect(screen.queryByLabelText("Brand colour hex")).toBeNull();
    expect(trigger.getAttribute("aria-expanded")).toBe("false");
    expect(trigger.textContent).toContain("#123456");
    // No focus steal: the pointer's own target keeps it.
    expect(document.activeElement).toBe(name);

    // A drag that STARTED on the picker and ran off its edge is not "outside".
    const hex = openColour("Brand colour");
    fireEvent.pointerDown(hex);
    fireEvent.click(name);
    expect(screen.getByLabelText("Brand colour hex")).toBeTruthy();
  });

  it("warns when no ink is readable on the candidate colour", async () => {
    renderWithQuery(<SchoolSettingsScreen />);

    await screen.findByRole("button", { name: /^Brand colour/ });
    fireEvent.change(openColour("Brand colour"), { target: { value: "#7e7e7e" } });
    expect(screen.getByText(/unreadable, no ink reaches 4.5:1/)).toBeTruthy();
  });

  it("hints a duplicate house code and a repeated colour, but never a pale one", async () => {
    renderWithQuery(<SchoolSettingsScreen />);

    const code = await screen.findByLabelText("House 2 code");
    fireEvent.change(code, { target: { value: "wattle" } });
    expect(houseRows()[1]!.textContent).toContain("listed twice");
    expect(houseRows()[0]!.textContent).not.toContain("listed twice");

    // One hint at a time, so the code has to be valid before the colour speaks.
    fireEvent.change(code, { target: { value: "MOUNTAIN ASH" } });
    const hex = openColour("House 2 colour");
    fireEvent.change(hex, { target: { value: "#b42318" } });
    expect(houseRows()[1]!.textContent).toContain("Another house already uses this colour");
    // A hint about what is still being typed is not an announcement.
    expect(houseRows()[1]!.querySelector('[role="alert"]')).toBeNull();

    // No contrast floor any more: a pale colour is saveable and says nothing.
    fireEvent.change(hex, { target: { value: "#ffff00" } });
    expect(houseRows()[1]!.querySelector(".field-error")).toBeNull();
  });

  it("adds a house, focuses its code, and removes one behind an armed confirm", async () => {
    renderWithQuery(<SchoolSettingsScreen />);

    fireEvent.click(await screen.findByRole("button", { name: "Add house" }));
    expect(houseRows()).toHaveLength(3);
    expect(document.activeElement).toBe(screen.getByLabelText("House 3 code"));

    // One tap arms, it does not remove, and focus follows the control replacing it.
    fireEvent.click(screen.getByRole("button", { name: "Remove Waratah" }));
    expect(houseRows()).toHaveLength(3);
    const confirm = screen.getByRole("button", { name: "Confirm removing Waratah" });
    expect(confirm.className).toContain("armed");
    expect(document.activeElement).toBe(confirm);

    // Cancelling hands focus back to the trigger it replaced.
    fireEvent.click(screen.getAllByRole("button", { name: "Cancel" })[0]!);
    expect(document.activeElement).toBe(screen.getByRole("button", { name: "Remove Waratah" }));

    fireEvent.click(screen.getByRole("button", { name: "Remove Waratah" }));
    fireEvent.click(screen.getByRole("button", { name: "Confirm removing Waratah" }));
    expect(houseRows()).toHaveLength(2);
    expect((screen.getByLabelText("House 1 code") as HTMLInputElement).value).toBe("MOUNTAIN ASH");
  });

  it("saves the whole replacement with the loaded stamp, then repaints the chrome", async () => {
    const saved = settings({
      brandColour: "#0b3d2c",
      updatedAtUtc: "2026-09-01T03:00:00.000Z",
    });
    vi.mocked(putSchoolSettings).mockResolvedValue(saved);
    renderWithQuery(<SchoolSettingsScreen />);

    await screen.findByRole("button", { name: /^Brand colour/ });
    fireEvent.change(openColour("Brand colour"), { target: { value: "#0b3d2c" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(putSchoolSettings).toHaveBeenCalled());
    expect(vi.mocked(putSchoolSettings).mock.calls[0]![0]).toEqual({
      name: "Riverbend Secondary College",
      brandColour: "#0b3d2c",
      houses: [
        { code: "WATTLE", name: "Waratah", colour: "#b42318" },
        { code: "MOUNTAIN ASH", name: "Mountain Ash", colour: "#1a7f37" },
      ],
      updatedAtUtc: STAMP,
    });
    await waitFor(() => expect(refreshBranding).toHaveBeenCalled());
    expect(refreshHouseMarks).toHaveBeenCalled();
    expect(getToasts().map((t) => t.message)).toContain("School settings saved");
    // The response is the new baseline, so there is nothing left to save.
    await waitFor(() =>
      expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(
        true,
      ),
    );
  });

  it("restores the loaded values on Cancel", async () => {
    renderWithQuery(<SchoolSettingsScreen />);

    const name = (await screen.findByLabelText("School name")) as HTMLInputElement;
    fireEvent.change(name, { target: { value: "Someone else's school" } });
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(name.value).toBe("Riverbend Secondary College");
  });

  it("lands a 422 on the row and field the server named", async () => {
    vi.mocked(putSchoolSettings).mockRejectedValue(
      new ApiError(
        422,
        {
          error: "HOUSE_COLOUR_DUPLICATE",
          detail: "'#b42318' is already used by WATTLE; each house needs its own colour.",
          field: "houses[1].colour",
        },
        "/school/settings",
      ),
    );
    renderWithQuery(<SchoolSettingsScreen />);

    fireEvent.change(await screen.findByLabelText("School name"), { target: { value: "Riverbend" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(houseRows()[1]!.textContent).toContain("already used by WATTLE"));
    expect(houseRows()[0]!.textContent).not.toContain("already used by WATTLE");

    // Announced, tied to the control it is about, and focused.
    const trigger = screen.getByRole("button", { name: /^House 2 colour/ });
    expect(trigger.getAttribute("aria-invalid")).toBe("true");
    const message = document.getElementById(trigger.getAttribute("aria-describedby")!)!;
    expect(message.getAttribute("role")).toBe("alert");
    expect(message.textContent).toContain("already used by WATTLE");
    expect(document.activeElement).toBe(trigger);
  });

  it("offers a reload when another administrator saved first", async () => {
    vi.mocked(putSchoolSettings).mockRejectedValue(
      new ApiError(
        409,
        { error: "SETTINGS_STALE", detail: "Another administrator saved these settings first." },
        "/school/settings",
      ),
    );
    renderWithQuery(<SchoolSettingsScreen />);

    fireEvent.change(await screen.findByLabelText("School name"), { target: { value: "Riverbend" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    const notice = await screen.findByText(/Someone else saved school settings first/);
    expect(notice).toBeTruthy();
    expect(getSchoolSettings).toHaveBeenCalledTimes(1);

    vi.mocked(getSchoolSettings).mockResolvedValue(
      settings({ name: "Riverbend Secondary", updatedAtUtc: "2026-09-01T04:00:00.000Z" }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Reload" }));

    await waitFor(() => expect(getSchoolSettings).toHaveBeenCalledTimes(2));
    await waitFor(() =>
      expect((screen.getByLabelText("School name") as HTMLInputElement).value).toBe(
        "Riverbend Secondary",
      ),
    );
  });

  it("names the role a non-administrator is missing, and never asks the server", async () => {
    signIn({ subject: "rec-1", displayName: "Olivia Official", roles: ["Recorder"] });
    renderWithQuery(<SchoolSettingsScreen />);

    expect(screen.getByRole("alert").textContent).toBe(
      "Administering school settings needs the system administrator role.",
    );
    expect(getSchoolSettings).not.toHaveBeenCalled();
    expect(screen.queryByRole("button", { name: "Save" })).toBeNull();
  });

  it("offers a sign-in to a signed-out visitor rather than naming a role", async () => {
    signOut();
    renderWithQuery(<SchoolSettingsScreen />);

    expect(await screen.findByRole("link", { name: "Sign in" })).toBeTruthy();
    expect(screen.queryByText(/system administrator role/)).toBeNull();
    expect(getSchoolSettings).not.toHaveBeenCalled();
  });

  it("cannot save while the server is out of reach", async () => {
    Object.defineProperty(navigator, "onLine", { value: false, configurable: true });
    renderWithQuery(<SchoolSettingsScreen />);

    fireEvent.change(await screen.findByLabelText("School name"), {
      target: { value: "Riverbend" },
    });
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(true);
    // Every section on the screen refuses offline in its own words (ADR-0050
    // adds one), so this is the school form's notice, not the only one.
    expect(screen.getAllByRole("status").map((n) => n.textContent).join()).toContain(
      "can’t be saved",
    );
  });

  /**
   * ADR-0045 §6: `inUse` is the server's own removal guard read ahead, so the
   * two edits it would refuse are shut off rather than offered and rejected.
   */
  it("locks removal and the code of a house data still references", async () => {
    vi.mocked(getSchoolSettings).mockResolvedValue(
      settings({
        houses: [
          { code: "WATTLE", name: "Waratah", colour: "#b42318", inUse: true },
          { code: "MOUNTAIN ASH", name: "Mountain Ash", colour: "#1a7f37", inUse: false },
        ],
      }),
    );
    renderWithQuery(<SchoolSettingsScreen />);

    const remove = (await screen.findByRole("button", {
      name: "Remove Waratah",
    })) as HTMLButtonElement;
    expect(remove.disabled).toBe(true);
    const code = screen.getByLabelText("House 1 code") as HTMLInputElement;
    expect(code.readOnly).toBe(true);

    // One visible note, named by both controls rather than hidden in a title.
    const noteId = code.getAttribute("aria-describedby")!;
    expect(remove.getAttribute("aria-describedby")).toBe(noteId);
    expect(document.getElementById(noteId)!.textContent).toContain(
      "in use — referenced by roster or carnival data",
    );

    // The display name and the colour are still the administrator's to change.
    expect((screen.getByLabelText("House 1 display name") as HTMLInputElement).readOnly).toBe(
      false,
    );
    expect(
      (screen.getByRole("button", { name: /^House 1 colour/ }) as HTMLButtonElement).disabled,
    ).toBe(false);
    expect(
      (screen.getByRole("button", { name: "Remove Mountain Ash" }) as HTMLButtonElement).disabled,
    ).toBe(false);
  });

  it("keeps typed edits when a refetch brings someone else's save", async () => {
    const { client } = renderWithQuery(<SchoolSettingsScreen />);

    fireEvent.change(await screen.findByLabelText("School name"), {
      target: { value: "Half-typed name" },
    });
    vi.mocked(getSchoolSettings).mockResolvedValue(
      settings({ name: "Someone else's save", updatedAtUtc: "2026-09-01T05:00:00.000Z" }),
    );
    await act(async () => {
      await client.invalidateQueries({ queryKey: keys.schoolSettings() });
    });

    expect(await screen.findByText(/Someone else saved school settings first/)).toBeTruthy();
    expect((screen.getByLabelText("School name") as HTMLInputElement).value).toBe(
      "Half-typed name",
    );
  });

  it("adopts a newer save when nothing has been typed", async () => {
    const { client } = renderWithQuery(<SchoolSettingsScreen />);
    await screen.findByLabelText("School name");

    vi.mocked(getSchoolSettings).mockResolvedValue(
      settings({ name: "Someone else's save", updatedAtUtc: "2026-09-01T05:00:00.000Z" }),
    );
    await act(async () => {
      await client.invalidateQueries({ queryKey: keys.schoolSettings() });
    });

    await waitFor(() =>
      expect((screen.getByLabelText("School name") as HTMLInputElement).value).toBe(
        "Someone else's save",
      ),
    );
    expect(screen.queryByText(/Someone else saved school settings first/)).toBeNull();
  });
});
