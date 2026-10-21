// @vitest-environment jsdom
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../api/client";
import { _resetReachabilityForTests } from "../api/reachability";
import type { Carnival, CarnivalSettings } from "../api/types";
import { signIn, signOut } from "../auth/session";
import { QueryWrapper, renderWithQuery } from "../test/queryWrapper";

/**
 * The divisions profile (ADR-0022 §2/§8, reduced to one age per division by
 * ADR-0058 §4). Three things it must never do: claim that editing the axes
 * changed the divisions, send a whole settings object that would take the
 * scoring rules and the lane count down with it, and drop a saved age the
 * roster no longer derives without saying so.
 */

vi.mock("../api/resources", () => ({
  getCarnival: vi.fn(),
  getCarnivalSettings: vi.fn(),
  updateCarnivalSettings: vi.fn(),
}));

import { getCarnival, getCarnivalSettings, updateCarnivalSettings } from "../api/resources";
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
  rosterYear: null,
  scoring: { placePoints: [8, 6, 4] } as CarnivalSettings["scoring"],
  adjustmentCategories: ["Team spirit"],
  laneCount: 6,
  autoFillLanes: false,
  divisions: {
    ageGroups: ["13"],
    categories: ["M", "F"],
    tiers: ["Competitive"],
  },
};

function select(name: "From" | "To"): HTMLSelectElement {
  return screen.getByLabelText(name) as HTMLSelectElement;
}

function sentProfile(): NonNullable<Parameters<typeof updateCarnivalSettings>[1]["divisions"]> {
  return vi.mocked(updateCarnivalSettings).mock.calls[0]![1].divisions!;
}

beforeEach(() => {
  cleanup();
  _resetReachabilityForTests();
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
  signIn({ subject: "admin-1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
  vi.mocked(getCarnival).mockReset().mockResolvedValue(carnival);
  vi.mocked(getCarnivalSettings).mockReset().mockResolvedValue(settings);
  vi.mocked(updateCarnivalSettings).mockReset().mockResolvedValue(settings);
});

afterEach(() => {
  signOut();
});

describe("DivisionsProfileScreen", () => {
  it("says plainly that editing the axes creates and removes no divisions", async () => {
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    const notice = await screen.findByText(/creates and removes no/);
    expect(notice.textContent).toContain("plain age as at 31 December");
  });

  it("sends only the divisions slice, never the scoring rules or the lane count", async () => {
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    fireEvent.change(await screen.findByLabelText("To"), { target: { value: "15" } });
    fireEvent.click(screen.getByRole("button", { name: "Save profile" }));

    await waitFor(() => expect(updateCarnivalSettings).toHaveBeenCalledTimes(1));
    expect(vi.mocked(updateCarnivalSettings).mock.calls[0]).toEqual([
      "c-1",
      {
        divisions: {
          ageGroups: ["13", "14", "15"],
          categories: ["M", "F"],
          tiers: ["Competitive"],
        },
      },
    ]);
  });

  it("saves a tier the carnival has just taken on", async () => {
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    fireEvent.click(await screen.findByLabelText("Social"));
    fireEvent.click(screen.getByRole("button", { name: "Save profile" }));

    await waitFor(() => expect(updateCarnivalSettings).toHaveBeenCalledTimes(1));
    expect(sentProfile().tiers).toEqual(["Competitive", "Social"]);
  });

  it("renders a refusal as a sentence naming the age and what a code must be", async () => {
    vi.mocked(updateCarnivalSettings).mockRejectedValue(
      new ApiError(
        400,
        {
          error: "AGE_SCOPE_INVALID",
          detail: "Age group 'Year 8' may contain only letters and digits.",
        },
        "/x",
      ),
    );
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    fireEvent.click(await screen.findByRole("button", { name: "Save profile" }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("Age group 'Year 8'");
    // Nothing on this screen is typed, so the only fix is an age to untick.
    expect(alert.textContent).toContain("Untick any age the roster no longer derives");
    expect(alert.textContent).not.toContain("AGE_SCOPE_INVALID");
  });

  it("passes an over-long settings blob refusal through as the server's sentence", async () => {
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

    fireEvent.click(await screen.findByRole("button", { name: "Save profile" }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("shorten the division profile");
    expect(alert.textContent).not.toContain("VALIDATION_FAILED");
  });

  it("treats a carnival with no profile as unconfigured, not broken", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue({ ...settings, divisions: null });
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    expect(await screen.findByText(/no divisions profile yet/)).toBeTruthy();
    expect(screen.queryByRole("alert")).toBeNull();
  });
});

/** ADR-0058 §4: From and To over the roster's own ages, plus a tick per other code. */
describe("DivisionsProfileScreen — the ages come from the roster", () => {
  const roster = ["12", "13", "14", "15"];
  const rostered: CarnivalSettings = {
    ...settings,
    divisions: null,
    rosterAgeGroups: roster,
    divisionsDefault: {
      ageGroups: roster,
      categories: ["M", "F"],
      tiers: ["Competitive"],
    },
  };

  it("opens on the roster's youngest and oldest ages when nothing is saved", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue(rostered);
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    expect(((await screen.findByLabelText("From")) as HTMLSelectElement).value).toBe("12");
    expect(select("To").value).toBe("15");
    expect(screen.getByText(/these ages are the roster/)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Save profile" }));
    await waitFor(() => expect(updateCarnivalSettings).toHaveBeenCalledTimes(1));
    expect(sentProfile()).toEqual({
      ageGroups: roster,
      categories: ["M", "F"],
      tiers: ["Competitive"],
    });
  });

  it("restores the saved profile's own ends of the ladder", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue({
      ...rostered,
      divisionsDefault: null,
      divisions: { ageGroups: ["13", "14"], categories: ["M"], tiers: ["Competitive"] },
    });
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    expect(((await screen.findByLabelText("From")) as HTMLSelectElement).value).toBe("13");
    expect(select("To").value).toBe("14");
  });

  it("enforces From ≤ To through the option lists rather than a refusal", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue(rostered);
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    fireEvent.change(await screen.findByLabelText("From"), { target: { value: "14" } });
    expect([...select("To").options].map((option) => option.value)).toEqual(["14", "15"]);
    expect([...select("From").options].map((option) => option.value)).toEqual(roster);
  });

  it("ticks a non-numeric roster code no range can reach, and saves it in key order", async () => {
    const codes = [...roster, "SNR"];
    vi.mocked(getCarnivalSettings).mockResolvedValue({
      ...rostered,
      rosterAgeGroups: codes,
      divisionsDefault: { ...rostered.divisionsDefault!, ageGroups: codes },
    });
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    const snr = (await screen.findByRole("checkbox", { name: "SNR" })) as HTMLInputElement;
    expect(snr.checked).toBe(true);

    fireEvent.change(screen.getByLabelText("From"), { target: { value: "13" } });
    fireEvent.click(screen.getByRole("button", { name: "Save profile" }));
    await waitFor(() => expect(updateCarnivalSettings).toHaveBeenCalledTimes(1));
    // Numbers ascending, then the code no range holds — `orderCodes`, which is
    // the server's own key order.
    expect(sentProfile().ageGroups).toEqual(["13", "14", "15", "SNR"]);
  });

  it("drops a senior code once it is unticked", async () => {
    const codes = [...roster, "SNR"];
    vi.mocked(getCarnivalSettings).mockResolvedValue({
      ...rostered,
      rosterAgeGroups: codes,
      divisionsDefault: { ...rostered.divisionsDefault!, ageGroups: codes },
    });
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    fireEvent.click(await screen.findByRole("checkbox", { name: "SNR" }));
    fireEvent.click(screen.getByRole("button", { name: "Save profile" }));
    await waitFor(() => expect(updateCarnivalSettings).toHaveBeenCalledTimes(1));
    expect(sentProfile().ageGroups).toEqual(roster);
  });

  it("names the roster ages the selection leaves out, and says nothing once none are", async () => {
    const codes = ["11", ...roster, "SNR"];
    vi.mocked(getCarnivalSettings).mockResolvedValue({
      ...rostered,
      rosterAgeGroups: codes,
      divisionsDefault: { ...rostered.divisionsDefault!, ageGroups: codes },
    });
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    await screen.findByLabelText("From");
    expect(screen.queryByText(/Roster ages not contested/)).toBeNull();

    fireEvent.change(screen.getByLabelText("From"), { target: { value: "12" } });
    expect(screen.getByText(/Roster ages not contested: 11/).textContent).toContain(
      "cannot be entered",
    );

    fireEvent.click(screen.getByRole("checkbox", { name: "SNR" }));
    expect(screen.getByText(/Roster ages not contested: 11, SNR/)).toBeTruthy();
  });

  // The failure ADR-0022 could only warn about in prose: a saved profile naming
  // a code the roster does not derive refuses every entry on deck day.
  it("names a saved age the roster no longer derives", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue({
      ...rostered,
      rosterAgeGroups: ["12", "13"],
      divisionsDefault: null,
      divisions: { ageGroups: ["12", "13", "14"], categories: ["M"], tiers: ["Competitive"] },
    });
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    expect(
      (await screen.findByText(/Saved ages the roster no longer derives: 14/)).textContent,
    ).toContain("still run");

    // The selects cannot offer it, so Save replaces it with what the roster has.
    expect([select("From").value, select("To").value]).toEqual(["12", "13"]);
    fireEvent.click(screen.getByRole("button", { name: "Save profile" }));
    await waitFor(() => expect(updateCarnivalSettings).toHaveBeenCalledTimes(1));
    expect(sentProfile().ageGroups).toEqual(["12", "13"]);
  });

  it("says nothing about saved ages when the roster still derives them all", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue({
      ...rostered,
      divisionsDefault: null,
      divisions: { ageGroups: ["13", "14"], categories: ["M"], tiers: ["Competitive"] },
    });
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    await screen.findByLabelText("From");
    expect(screen.queryByText(/Saved ages the roster no longer derives/)).toBeNull();
    expect(screen.queryByText(/saving replaces them/)).toBeNull();
  });

  // A gapped set is one the From/To pair cannot restate, so saving widens it —
  // which the screen says before the press rather than after.
  it("warns that a gapped saved set is replaced by the range", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue({
      ...rostered,
      divisionsDefault: null,
      divisions: { ageGroups: ["12", "13", "15"], categories: ["M"], tiers: ["Competitive"] },
    });
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    expect(
      await screen.findByText("Saved ages: 12, 13, 15 — saving replaces them with the range below."),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Save profile" }));
    await waitFor(() => expect(updateCarnivalSettings).toHaveBeenCalledTimes(1));
    expect(sentProfile().ageGroups).toEqual(["12", "13", "14", "15"]);
  });

  it("falls back to ages 5 to 21 when no roster is promoted", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue({ ...settings, divisions: null });
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    expect(await screen.findByText(/No roster is promoted yet/)).toBeTruthy();
    expect([select("From").value, select("To").value]).toEqual(["5", "21"]);
    // No roster means no code to tick, and nothing for one to be absent from.
    expect(screen.queryByRole("heading", { name: "Other ages" })).toBeNull();
    expect(screen.queryByText(/Roster ages not contested/)).toBeNull();
    expect(screen.queryByText(/Saved ages the roster no longer derives/)).toBeNull();
  });
});

describe("DivisionsProfileScreen — the read has to land first", () => {
  // An empty pre-filled form beside the error alert is a Save away from
  // replacing the real profile with nothing.
  it("offers no form at all when the settings read fails", async () => {
    vi.mocked(getCarnivalSettings).mockRejectedValue(new TypeError("Failed to fetch"));
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("couldn’t be read");
    expect(screen.queryByRole("button", { name: "Save profile" })).toBeNull();
    expect(screen.queryByLabelText("From")).toBeNull();
  });

  // The draft is seeded once per mount, so without the key the second carnival
  // would be edited with the first one's ages.
  it("re-seeds the draft when the screen moves to another carnival", async () => {
    const { client, rerender } = renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);
    expect(((await screen.findByLabelText("From")) as HTMLSelectElement).value).toBe("13");

    vi.mocked(getCarnivalSettings).mockResolvedValue({
      ...settings,
      divisions: { ageGroups: ["16", "17"], categories: ["M", "F"], tiers: ["Competitive"] },
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
    vi.mocked(getCarnival).mockResolvedValue({ ...carnival, status: "Finalised" });
    renderWithQuery(<DivisionsProfileScreen carnivalId="c-1" />);

    await screen.findByText(/no longer be changed/);
    const save = screen.getByRole("button", { name: "Save profile" }) as HTMLButtonElement;
    expect(save.disabled).toBe(true);
    expect(select("From").disabled).toBe(true);
  });
});
