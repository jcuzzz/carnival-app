// @vitest-environment jsdom
import { cleanup, screen } from "@testing-library/react";
import type { ComponentType } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { signIn, signOut } from "../auth/session";
import { renderWithQuery } from "../test/queryWrapper";

vi.mock("../features/roles/RolesSection", () => ({
  default: () => <p>roles section</p>,
}));
vi.mock("../features/templates/TemplatesSection", () => ({
  default: () => <p>templates section</p>,
}));

import SchoolRolesScreen from "./SchoolRolesScreen";
import SchoolTemplatesScreen from "./SchoolTemplatesScreen";

const screens: [string, ComponentType, string, RegExp][] = [
  ["SchoolRolesScreen", SchoolRolesScreen, "roles section", /Administering roles needs/],
  [
    "SchoolTemplatesScreen",
    SchoolTemplatesScreen,
    "templates section",
    /Applying an event template needs/,
  ],
];

beforeEach(() => {
  cleanup();
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
});

afterEach(() => {
  cleanup();
  signOut();
});

describe.each(screens)("%s", (_name, Screen, section, refusal) => {
  it("offers a sign-in to a signed-out visitor rather than naming a role", async () => {
    signOut();
    renderWithQuery(<Screen />);

    expect(await screen.findByRole("link", { name: "Sign in" })).toBeTruthy();
    expect(screen.queryByText(refusal)).toBeNull();
    expect(screen.queryByText(section)).toBeNull();
  });

  it("names the missing role to a signed-in visitor without it", async () => {
    signIn({ subject: "s2", displayName: "Rae Recorder", roles: ["Recorder"] });
    renderWithQuery(<Screen />);

    expect(await screen.findByText(refusal)).toBeTruthy();
    expect(screen.queryByRole("link", { name: "Sign in" })).toBeNull();
  });

  it("shows the section to a system administrator", async () => {
    signIn({ subject: "s1", displayName: "Sam Sysadmin", roles: ["SystemAdministrator"] });
    renderWithQuery(<Screen />);

    expect(await screen.findByText(section)).toBeTruthy();
  });
});
