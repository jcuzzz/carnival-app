// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { StandingsTable } from "./StandingsTable";
import type { CarnivalSettings, Standings } from "../../api/types";

/**
 * ADR-0043, owner ruling 2026-09-20: the rank is the server's and tied houses
 * share it. The array index used to decide it, so two houses on equal totals
 * read 1 and 2 with identical bars beside them — on the Dashboard and, printed,
 * in the reports tier.
 */

type House = Standings["houses"][number];

function house(houseCode: string, rank: number, total: number): House {
  return {
    houseCode,
    rank,
    total,
    placementPoints: total,
    participationPoints: 0,
    bonusPoints: 0,
  };
}

function standings(houses: House[]): Standings {
  return {
    carnivalId: "c-1",
    ruleVersion: "v1",
    provisional: true,
    eventsCounted: 2,
    resultsCounted: 5,
    houses,
    weightedEvents: [],
    hasSocialEvents: false,
  };
}

const ranks = () =>
  screen.getAllByRole("listitem").map((row) => row.querySelector(".standings-rank")?.textContent);

describe("StandingsTable", () => {
  it("shares a rank between tied houses and skips the next", () => {
    render(
      <StandingsTable
        data={standings([house("BANKSIA", 1, 9), house("WATTLE", 1, 9), house("GREVILLEA", 3, 4)])}
        settings={undefined}
        colour
      />,
    );
    expect(ranks()).toEqual(["=1", "=1", "3"]);
  });

  it("numbers separated houses plainly", () => {
    render(
      <StandingsTable
        data={standings([house("BANKSIA", 1, 9), house("WATTLE", 2, 6), house("GREVILLEA", 3, 4)])}
        settings={undefined}
        colour
      />,
    );
    expect(ranks()).toEqual(["1", "2", "3"]);
  });

  it("reads the same in the reports tier, which renders no house colour", () => {
    render(
      <StandingsTable
        data={standings([house("BANKSIA", 1, 9), house("WATTLE", 1, 9), house("GREVILLEA", 3, 4)])}
        settings={undefined}
        colour={false}
      />,
    );
    expect(ranks()).toEqual(["=1", "=1", "3"]);
    expect(screen.getByText("BANKSIA")).toBeTruthy();
  });

  it("lists a house that has not scored at 0, with an empty bar, sharing its rank", () => {
    render(
      <StandingsTable
        data={standings([house("BANKSIA", 1, 4), house("GREVILLEA", 2, 0), house("WATTLE", 2, 0)])}
        settings={undefined}
        colour
      />,
    );
    expect(ranks()).toEqual(["1", "=2", "=2"]);
    const grevillea = screen.getAllByRole("listitem")[1];
    expect(grevillea.querySelector(".standings-total")?.textContent).toBe("0");
    expect((grevillea.querySelector(".standings-bar") as HTMLElement).style.width).toBe("0%");
  });

  it("puts each weighted definition and the Social points in the legend", () => {
    render(
      <StandingsTable
        data={{
          ...standings([house("BANKSIA", 1, 0)]),
          weightedEvents: [{ definitionName: "Freestyle Relay", weight: 3 }],
          hasSocialEvents: true,
        }}
        settings={{
          scoring: {
            version: "v1",
            placePoints: [4, 3, 2],
            participationFloor: 1,
            dnfPoints: 1,
            dnsPoints: 0,
            dqPoints: 0,
            socialPerEntryPoints: 1,
          },
        } as CarnivalSettings}
      />,
    );
    expect(screen.getByText(/Freestyle Relay ×3 · Social 1 per entry/)).toBeTruthy();
  });
});
