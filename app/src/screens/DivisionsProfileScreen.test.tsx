// @vitest-environment jsdom
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../api/client";
import { _resetReachabilityForTests } from "../api/reachability";
import type { Carnival, CarnivalSettings } from "../api/types";
import { signIn, signOut } from "../auth/session";
import { QueryWrapper, renderWithQuery } from "../test/queryWrapper";

/**
 * The ages editor. Things it must never do: send a whole settings object that would take the
 * scoring rules and the lane count down with it, save an oldest age that is not open-ended, or
 * replace a saved set it cannot restate without saying so.
 */

vi.mock("../api/resources", () => ({
  getCarnival: vi.fn(),
  getCarnivalSettings: vi.fn(),
  updateCarnivalSettings: vi.fn(),
  extendCarnivalAges: vi.fn(),
}));

import {
  extendCarnivalAges,
  getCarnival,
  getCarnivalSettings,
  updateCarnivalSettings,
} from "../api/resources";
import DivisionsProfileScreen from "./DivisionsProfileScreen";

const carnival = {
  id: "c-1",
  name: "Swimming Carnival 2027",
  date: "2027-02-19",
  status: "Open",
  type: "Swimming",
} as Carnival;

const settings: CarnivalSettings = {
  divisionsDefault: null,
  rosterAgeGroups: [],
  uncoveredAges: [],
  agesWithoutStudents: [],
  rosterYear: null,
  scoring: { placePoints: [8, 6, 4] } as CarnivalSettings["scoring"],
  adjustmentCategories: ["Team spirit"],
  laneCount: 6,
  autoFillLanes: false,
  divisions: {
    ageGroups: ["13", "14+"],
    categories: ["M", "F"],
    tiers: ["Competitive"],
  },
};

function select(name: "From" | "To"): HTMLSelectElement {
  return screen.getByLabelText(
    name === "To" ? "To (and over)" : name,
  ) as HTMLSelectElement;
}

function values(name: "From" | "To"): string[] {
  return [...select(name).options].map((option) => option.value);
}

function sentProfile(): NonNullable<
  Parameters<typeof updateCarnivalSettings>[1]["divisions"]
> {
  return vi.mocked(updateCarnivalSettings).mock.calls[0]![1].divisions!;
}

beforeEach(() => {
  cleanup();
  _resetReachabilityForTests();
  Object.defineProperty(navigator, "onLine", {
    value: true,
    configurable: true,
  });
  signIn({
    subject: "admin-1",
    displayName: "Ada Admin",
    roles: ["CarnivalAdministrator"],
  });
  vi.mocked(getCarnival).mockReset().mockResolvedValue(carnival);
  vi.mocked(getCarnivalSettings).mockReset().mockResolvedValue(settings);
  vi.mocked(updateCarnivalSettings).mockReset().mockResolvedValue(settings);
  vi.mocked(extendCarnivalAges)
    .mockReset()
    .mockResolvedValue({ addedAges: [], divisionsAdded: 0 });
});

afterEach(() => {
  signOut();
});

describe("DivisionsProfileScreen", () => {
  it("says the ages start as the school's", async () => {
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    expect(
      await screen.findByRole("heading", { name: "Ages and categories" }),
    ).toBeTruthy();
    expect(
      screen.getByText(/^This carnival’s ages start as the school’s/),
    ).toBeTruthy();
    expect(screen.queryByText(/student list/)).toBeNull();
  });

  it("sends only the divisions slice, with every age plain but the oldest", async () => {
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    fireEvent.change(await screen.findByLabelText("To (and over)"), {
      target: { value: "16" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(updateCarnivalSettings).toHaveBeenCalledTimes(1),
    );
    expect(vi.mocked(updateCarnivalSettings).mock.calls[0]).toEqual([
      "c-1",
      {
        divisions: {
          ageGroups: ["13", "14", "15", "16+"],
          categories: ["M", "F"],
          tiers: ["Competitive"],
        },
      },
    ]);
  });

  it("saves a tier the carnival has just taken on", async () => {
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    fireEvent.click(await screen.findByLabelText("Social"));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(updateCarnivalSettings).toHaveBeenCalledTimes(1),
    );
    expect(sentProfile().tiers).toEqual(["Competitive", "Social"]);
  });

  it("renders a refusal as the server's sentence, never its code", async () => {
    vi.mocked(updateCarnivalSettings).mockRejectedValue(
      new ApiError(
        400,
        {
          error: "VALIDATION_FAILED",
          detail:
            "The carnival's settings would exceed 4000 characters; shorten the division profile.",
        },
        "/x",
      ),
    );
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    fireEvent.click(await screen.findByRole("button", { name: "Save" }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("shorten the division profile");
    expect(alert.textContent).not.toContain("VALIDATION_FAILED");
  });
});

describe("DivisionsProfileScreen — From and To", () => {
  const school: CarnivalSettings = {
    ...settings,
    divisions: null,
    divisionsDefault: {
      ageGroups: ["12", "13", "14", "15", "16", "17", "18+"],
      categories: ["M", "F"],
      tiers: ["Competitive"],
    },
  };

  it("opens on the school's ages when nothing is saved, oldest and over", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue(school);
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    expect(
      ((await screen.findByLabelText("From")) as HTMLSelectElement).value,
    ).toBe("12");
    expect(select("To").value).toBe("18");
    expect(select("To").selectedOptions[0]!.textContent).toBe("18+");
    expect(screen.getByText(/these are the school’s ages/)).toBeTruthy();
    expect(screen.getByText(/^12 to 18\+: one age per division/)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() =>
      expect(updateCarnivalSettings).toHaveBeenCalledTimes(1),
    );
    expect(sentProfile()).toEqual(school.divisionsDefault);
  });

  it("offers plain ages from 3 to 25, not the student list's, keeping From ≤ To", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue({
      ...school,
      rosterAgeGroups: ["13"],
    });
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    await screen.findByLabelText("From");
    expect(values("From")[0]).toBe("3");
    expect(values("To").at(-1)).toBe("25");
    fireEvent.change(select("From"), { target: { value: "16" } });
    expect(values("To")[0]).toBe("16");
    fireEvent.change(select("To"), { target: { value: "17" } });
    expect(values("From").at(-1)).toBe("17");

    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() =>
      expect(updateCarnivalSettings).toHaveBeenCalledTimes(1),
    );
    expect(sentProfile().ageGroups).toEqual(["16", "17+"]);
  });

  it("adds a lower From to the events as Add age does, then saves the rest", async () => {
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    fireEvent.change(await screen.findByLabelText("From"), {
      target: { value: "11" },
    });
    expect(
      screen.getByText(/Saving also adds the younger ages to every event/),
    ).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() =>
      expect(updateCarnivalSettings).toHaveBeenCalledTimes(1),
    );
    expect(extendCarnivalAges).toHaveBeenCalledWith("c-1", 11);
    expect(sentProfile().ageGroups).toEqual(["11", "12", "13", "14+"]);
  });

  it("saves unchanged ages without adding any to the events", async () => {
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    fireEvent.click(await screen.findByRole("button", { name: "Save" }));
    await waitFor(() =>
      expect(updateCarnivalSettings).toHaveBeenCalledTimes(1),
    );
    expect(extendCarnivalAges).not.toHaveBeenCalled();
  });

  it("saves a single age as open-ended", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue(school);
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    fireEvent.change(await screen.findByLabelText("From"), {
      target: { value: "18" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() =>
      expect(updateCarnivalSettings).toHaveBeenCalledTimes(1),
    );
    expect(sentProfile().ageGroups).toEqual(["18+"]);
  });

  it("offers no form with no ages anywhere, and points at School settings", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue({
      ...settings,
      divisions: null,
    });
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    expect(
      await screen.findByText(
        "A system administrator sets the school's ages in School settings.",
      ),
    ).toBeTruthy();
    expect(screen.queryByLabelText("From")).toBeNull();
    expect(screen.queryByRole("button", { name: "Save" })).toBeNull();
  });

  it("reads a saved legacy profile whose oldest age is plain, and says saving changes it", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue({
      ...settings,
      divisions: {
        ageGroups: ["12", "13", "14", "SNR"],
        categories: ["M"],
        tiers: ["Competitive"],
      },
    });
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    expect(
      await screen.findByText(
        "Saved ages: 12–14, SNR. Saving replaces them with 12 to 14+.",
      ),
    ).toBeTruthy();
    expect([select("From").value, select("To").value]).toEqual(["12", "14"]);
    expect(screen.queryByRole("checkbox", { name: "SNR" })).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() =>
      expect(updateCarnivalSettings).toHaveBeenCalledTimes(1),
    );
    expect(sentProfile().ageGroups).toEqual(["12", "13", "14+"]);
  });

  it("says nothing about saved ages the range restates", async () => {
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    await screen.findByLabelText("From");
    expect([select("From").value, select("To").value]).toEqual(["13", "14"]);
    expect(screen.queryByText(/Saved ages/)).toBeNull();
  });

  it("warns that a gapped saved set is replaced by the range", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue({
      ...settings,
      divisions: {
        ageGroups: ["12", "13", "15+"],
        categories: ["M"],
        tiers: ["Competitive"],
      },
    });
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    expect(
      await screen.findByText(
        "Saved ages: 12, 13, 15+. Saving replaces them with 12 to 15+.",
      ),
    ).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() =>
      expect(updateCarnivalSettings).toHaveBeenCalledTimes(1),
    );
    expect(sentProfile().ageGroups).toEqual(["12", "13", "14", "15+"]);
  });
});

describe("DivisionsProfileScreen — the read has to land first", () => {
  // An empty pre-filled form beside the error alert is a Save away from
  // replacing the real profile with nothing.
  it("offers no form at all when the settings read fails", async () => {
    vi.mocked(getCarnivalSettings).mockRejectedValue(
      new TypeError("Failed to fetch"),
    );
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("couldn’t be read");
    expect(screen.queryByRole("button", { name: "Save" })).toBeNull();
    expect(screen.queryByLabelText("From")).toBeNull();
  });

  // The draft is seeded once per mount, so without the key the second carnival
  // would be edited with the first one's ages.
  it("re-seeds the draft when the screen moves to another carnival", async () => {
    const { client, rerender } = renderWithQuery(
      <DivisionsProfileScreen carnivalId="c-1" />,
    );
    expect(
      ((await screen.findByLabelText("From")) as HTMLSelectElement).value,
    ).toBe("13");

    vi.mocked(getCarnivalSettings).mockResolvedValue({
      ...settings,
      divisions: {
        ageGroups: ["16", "17+"],
        categories: ["M", "F"],
        tiers: ["Competitive"],
      },
    });
    rerender(
      <QueryWrapper client={client}>
        <DivisionsProfileScreen carnivalId="c-2" />
      </QueryWrapper>,
    );

    await waitFor(() => expect(select("From").value).toBe("16"));
    expect(select("To").value).toBe("17");
  });
});

describe("DivisionsProfileScreen — read-only", () => {
  it("is read-only for a finalised carnival", async () => {
    vi.mocked(getCarnival).mockResolvedValue({
      ...carnival,
      status: "Finalised",
    });
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    await screen.findByText(/no longer be changed/);
    const save = screen.getByRole("button", {
      name: "Save",
    }) as HTMLButtonElement;
    expect(save.disabled).toBe(true);
    expect(select("From").disabled).toBe(true);
  });
});
