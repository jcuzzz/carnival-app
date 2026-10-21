// @vitest-environment jsdom
import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../../api/client";
import { _resetReachabilityForTests } from "../../api/reachability";
import type { TemplateDiff, TemplateDiffRow, TemplateReconcileOutcome } from "../../api/types";
import { _resetToastsForTests, getToasts } from "../../shell/toast";
import { renderWithQuery } from "../../test/queryWrapper";

/**
 * ADR-0050 §9. The screen previews and applies; every rule — what a template
 * owns, what a guard freezes, whether the preview is still current — is the
 * server's, so the assertions here are about rendering a refusal, never about
 * reproducing one.
 */

vi.mock("../../api/resources", () => ({
  listEventTemplates: vi.fn(),
  previewTemplateReconcile: vi.fn(),
  applyTemplateReconcile: vi.fn(),
}));

import {
  applyTemplateReconcile,
  listEventTemplates,
  previewTemplateReconcile,
} from "../../api/resources";
import TemplatesSection from "./TemplatesSection";

function row(overrides: Partial<TemplateDiffRow> = {}): TemplateDiffRow {
  return {
    code: "SW-50-FREE",
    name: "Freestyle 50m",
    kind: "Update",
    fields: [{ field: "name", prior: "50m Freestyle", new: "Freestyle 50m", guard: null }],
    updatedAtUtc: null,
    editedInApp: false,
    guarded: false,
    ...overrides,
  };
}

function diff(rows: TemplateDiffRow[], hash = "hash-1"): TemplateDiff {
  return {
    templateId: "swimming",
    templateName: "Swimming carnival",
    templateVersion: "swimming.json",
    diffHash: hash,
    rows,
    counts: {
      unchanged: rows.filter((r) => r.kind === "Unchanged").length,
      update: rows.filter((r) => r.kind === "Update").length,
      missing: rows.filter((r) => r.kind === "Missing").length,
      catalogueOnly: rows.filter((r) => r.kind === "CatalogueOnly").length,
      guarded: rows.filter((r) => r.guarded).length,
    },
  };
}

const OUTCOME: TemplateReconcileOutcome = {
  templateId: "swimming",
  templateName: "Swimming carnival",
  templateVersion: "swimming.json",
  diffHash: "hash-1",
  reason: "Category-first names.",
  updated: 1,
  created: 1,
  unchanged: 0,
  catalogueOnly: 0,
  updatedCodes: ["SW-50-FREE"],
  createdCodes: ["SW-50-FLY"],
  excludedCodes: [],
  ignoredCodes: [],
};

beforeEach(() => {
  cleanup();
  _resetToastsForTests();
  _resetReachabilityForTests();
  vi.mocked(listEventTemplates)
    .mockReset()
    .mockResolvedValue([
      { id: "swimming", name: "Swimming carnival", version: "swimming.json", definitions: 8, readable: true },
    ]);
  vi.mocked(previewTemplateReconcile).mockReset().mockResolvedValue(diff([row()]));
  vi.mocked(applyTemplateReconcile).mockReset().mockResolvedValue(OUTCOME);
});

afterEach(() => {
  cleanup();
  _resetToastsForTests();
});

async function readyToApply(): Promise<HTMLButtonElement> {
  return (await screen.findByRole("button", { name: "Apply template" })) as HTMLButtonElement;
}

describe("TemplatesSection", () => {
  it("previews the first readable template without being asked", async () => {
    renderWithQuery(<TemplatesSection offline={false} />);
    await waitFor(() =>
      expect(vi.mocked(previewTemplateReconcile).mock.calls[0]?.[0]).toBe("swimming"),
    );
    expect(await screen.findByText("swimming.json")).toBeTruthy();
  });

  it("shows an update field by field, prior beside the template's value", async () => {
    renderWithQuery(<TemplatesSection offline={false} />);
    const table = (await screen.findByRole("table")) as HTMLTableElement;

    expect(within(table).getByText("SW-50-FREE")).toBeTruthy();
    expect(within(table).getByText("Name")).toBeTruthy();
    expect(within(table).getByText("50m Freestyle")).toBeTruthy();
    // The template's value renders in the last cell; the event's own name is
    // beside its code, so the string appears twice by design.
    expect(within(table).getAllByText("Freestyle 50m").length).toBe(2);
  });

  it("marks a missing code as one that will be created", async () => {
    vi.mocked(previewTemplateReconcile).mockResolvedValue(
      diff([row({ code: "SW-50-FLY", name: "Butterfly 50m", kind: "Missing", fields: [] })]),
    );
    renderWithQuery(<TemplatesSection offline={false} />);
    expect(await screen.findByText("will be created")).toBeTruthy();
  });

  it("lists a catalogue-only code as untouched rather than acting on it", async () => {
    vi.mocked(previewTemplateReconcile).mockResolvedValue(
      diff([row(), row({ code: "AT-100-SPRINT", kind: "CatalogueOnly", fields: [] })]),
    );
    renderWithQuery(<TemplatesSection offline={false} />);
    expect(await screen.findByText(/Untouched — in the catalogue but not in this template/))
      .toBeTruthy();
    expect(screen.getByText(/AT-100-SPRINT/)).toBeTruthy();
  });

  it("shows a guard against its row and leaves that code out of the apply", async () => {
    vi.mocked(previewTemplateReconcile).mockResolvedValue(
      diff([
        row({
          code: "SW-50-BACK",
          guarded: true,
          fields: [
            {
              field: "units",
              prior: "s",
              new: "ms",
              guard: "A result has been recorded against this event, so its units are frozen.",
            },
          ],
        }),
        row(),
      ]),
    );
    renderWithQuery(<TemplatesSection offline={false} />);
    expect(await screen.findByText(/its units are frozen/)).toBeTruthy();
    expect(screen.getByText("cannot change")).toBeTruthy();

    fireEvent.change(screen.getByLabelText(/^Reason for applying/), {
      target: { value: "Category-first names." },
    });
    fireEvent.click(await readyToApply());

    await waitFor(() =>
      expect(vi.mocked(applyTemplateReconcile).mock.calls[0]).toEqual([
        "swimming",
        "hash-1",
        "Category-first names.",
        ["SW-50-BACK"],
      ]),
    );
  });

  it("marks a row the app has edited, without guessing what that means", async () => {
    vi.mocked(previewTemplateReconcile).mockResolvedValue(
      diff([row({ editedInApp: true, updatedAtUtc: "2026-09-04T01:00:00.000Z" })]),
    );
    renderWithQuery(<TemplatesSection offline={false} />);
    expect(await screen.findByText(/^edited in the app/)).toBeTruthy();
  });

  it("leaves a row out on request and sends its code as an exclusion", async () => {
    renderWithQuery(<TemplatesSection offline={false} />);
    fireEvent.click(await screen.findByLabelText("Leave out"));
    fireEvent.change(screen.getByLabelText(/^Reason for applying/), {
      target: { value: "Only the new events." },
    });
    // Nothing left to apply once the only changing row is out.
    expect((await readyToApply()).disabled).toBe(true);
  });

  it("cannot apply until a reason is typed", async () => {
    renderWithQuery(<TemplatesSection offline={false} />);
    const apply = await readyToApply();
    expect(apply.disabled).toBe(true);

    fireEvent.change(screen.getByLabelText(/^Reason for applying/), {
      target: { value: "Category-first names." },
    });
    expect(apply.disabled).toBe(false);
  });

  it("says so, and offers nothing to apply, when the catalogue already matches", async () => {
    vi.mocked(previewTemplateReconcile).mockResolvedValue(
      diff([row({ kind: "Unchanged", fields: [] })]),
    );
    renderWithQuery(<TemplatesSection offline={false} />);
    expect(await screen.findByText(/The catalogue already matches this template/)).toBeTruthy();
    expect((await readyToApply()).disabled).toBe(true);
  });

  it("reports what an apply changed, naming the codes", async () => {
    renderWithQuery(<TemplatesSection offline={false} />);
    fireEvent.change(await screen.findByLabelText(/^Reason for applying/), {
      target: { value: "Category-first names." },
    });
    fireEvent.click(await readyToApply());

    expect(await screen.findByText(/Swimming carnival applied — 1 changed, 1 created\./))
      .toBeTruthy();
    expect(screen.getByText(/Changed: SW-50-FREE/)).toBeTruthy();
    expect(screen.getByText(/Created: SW-50-FLY/)).toBeTruthy();
    expect(getToasts().map((t) => t.message).join()).toContain("2 event(s) changed");
  });

  it("re-reads the preview when the server says it is stale", async () => {
    vi.mocked(applyTemplateReconcile).mockRejectedValue(
      new ApiError(
        409,
        { error: "TEMPLATE_RECONCILE_STALE", detail: "The catalogue has changed." },
        "/admin/event-templates/swimming/reconcile",
      ),
    );
    renderWithQuery(<TemplatesSection offline={false} />);
    fireEvent.change(await screen.findByLabelText(/^Reason for applying/), {
      target: { value: "Category-first names." },
    });
    fireEvent.click(await readyToApply());

    expect(await screen.findByText(/changed since this preview was taken/)).toBeTruthy();
    await waitFor(() =>
      expect(vi.mocked(previewTemplateReconcile).mock.calls.length).toBeGreaterThan(1),
    );
  });

  it("refuses plainly offline, without pretending the change is saved", async () => {
    renderWithQuery(<TemplatesSection offline={true} />);
    expect(await screen.findByText(/Can’t reach the server, so a template can’t be previewed/))
      .toBeTruthy();
    fireEvent.change(await screen.findByLabelText(/^Reason for applying/), {
      target: { value: "Category-first names." },
    });
    expect((await readyToApply()).disabled).toBe(true);
  });

  it("offers an unreadable template as unselectable rather than hiding it", async () => {
    vi.mocked(listEventTemplates).mockResolvedValue([
      { id: "swimming", name: "Swimming carnival", version: "swimming.json", definitions: 8, readable: true },
      { id: "athletics", name: null, version: "athletics.json", definitions: 0, readable: false },
    ]);
    renderWithQuery(<TemplatesSection offline={false} />);
    const broken = (await screen.findByRole("option", {
      name: /athletics — cannot be read/,
    })) as HTMLOptionElement;
    expect(broken.disabled).toBe(true);
  });
});
