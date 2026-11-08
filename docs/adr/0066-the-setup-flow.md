# ADR-0066: The setup flow — school first, then the carnival, and the Deck only on the day

- **Status:** Accepted (2026-11). Partly supersedes [ADR-0024](0024-carnival-context-and-primary-navigation.md) §5 (only the line a non-manager sees on an empty Deck), [ADR-0054](0054-setting-up-a-carnival.md) §1, §2 and §4 (the checklist's steps, the Deck's empty state linking to the first undone step, where Apply template sits, which open is primary) and [ADR-0060](0060-the-competition-year-is-chosen-at-import.md) §3 (the year an import from a carnival pre-fills). Divisions leave the checklist under [ADR-0067](0067-divisions-come-from-the-student-list.md).

## Context

Setup was reachable but had no shape. After the first administrator's account was created, the app showed an empty carnival list, and the School hub was seven unordered links. A new carnival landed on the Deck, whose empty state was the label of the next checklist step, so "Add divisions" became the Deck's primary button. The roster import was reachable from School, and from Manage's checklist only for a System administrator. It defaulted its year to the earliest upcoming carnival's, and led nowhere afterwards. The primary "Open carnival" left every division Scheduled, so no results could be recorded. Officials were assigned on the Deck by typing subject ids that no screen shows.

The maintainer asked for this order: create the account, configure the school, create a carnival, and import the student list into it. Research on meet-management tools and onboarding agrees: one order, small task lists with done-states derived from data, empty states that link to the action, and a check before the commit ([research](../research/2026-11-07-setup-flow.md)).

## Decision

1. **The first run creates the account and nothing else.** The setup link asks for a username, display name and password. It then offers the authenticator ([ADR-0065](0065-built-in-local-accounts.md) §5) and lands on **School**.
2. **School opens with "Set up your school"** for a System administrator while a required row is undone. It has three rows: school name, colours and houses; staff accounts (optional); your first carnival. The school row is done once an administrator has saved School settings; a name or houses seeded from configuration do not count, and a new install seeds no sample school or houses. The other rows' done-states are derived from data. Done rows stay listed with a quiet badge while the list shows. A set-up school shows nothing of it: the hub speaks for itself. Adding a person names their role in the same form.
3. **A new carnival lands on Manage.** The landing rule ([ADR-0024](0024-carnival-context-and-primary-navigation.md) §5) applies from the moment of creation.
4. **Carnival setup has four steps, in order: Students, Events, Program (optional), Open.** Back links from the setup screens lead to Manage.
   - **Students** is done when a promoted roster has students. For a System administrator it links to the roster import launched from this carnival. The import pre-fills the carnival's year and offers a way back afterwards. The student list stays school-wide ([ADR-0060](0060-the-competition-year-is-chosen-at-import.md)): the carnival supplies only the default year, so a second carnival in the same year finds the step done. The step keeps the roster-year warning ([ADR-0060](0060-the-competition-year-is-chosen-at-import.md) §4), and Ready to open repeats it. School → Roster import stays, for re-imports during the year.
   - **Events**: "Add the standard events" (the template apply) sits on Event setup rather than as a section of Manage. Its endpoint and permissions are unchanged. Ages and categories follow [ADR-0067](0067-divisions-come-from-the-student-list.md).
5. **Ready to open.** Before Open, Manage lists warnings from what it already reads:
   - a roster year that differs from the carnival's year;
   - the count of events and divisions;
   - divisions whose age no current student has;
   - divisions outside the program.

   It never blocks.
6. **The primary "Open carnival" opens the carnival and every Scheduled division.** "Open the carnival only" is the secondary, for schools that open events as they are called. Per-division Open and Close remain on the Deck.
7. **The Deck is for the day.** It holds capture and per-division Open and Close, and nothing that sets a carnival up.
   - An empty Deck shows a manager one link, "Set the carnival up", to Manage, as [ADR-0024](0024-carnival-context-and-primary-navigation.md) §5 says, rather than the next step's label. Anyone else sees a line saying the carnival is not set up yet.
   - The Deck no longer offers officials assignment. The assignment API stays, and existing assignments still drive "My events" ([ADR-0016](0016-role-model-simplification.md)). A picker in setup is built only if staff ask for "My events".
8. **A roster review ends with one press that approves and promotes.** The batch still passes through Approved, and both audit events are written. A failure between the two leaves an Approved batch, which the screen offers to promote. Reject stays its own press.

How it looks: [app navigation](../architecture/app-navigation.md), [roster import](../architecture/imports.md).

## Consequences

- A first-time administrator always has a next step, and the Deck no longer teaches setup.
- An install whose name and houses were seeded from `.env` shows the school row undone until an administrator opens School settings and saves, even with nothing changed.
- An organiser who presses the primary out of habit opens every division. Closing a division is the undo.
- The app offers no way to assign officials, so "My events" shows nothing on new carnivals. The API remains, and a picker in setup is built only if staff ask for "My events".
- A Carnival administrator still cannot import. The Students step names who can.
- The separate approve press, a pause before the roster changes, is gone. The review table stays above the button.

## Rejected alternatives

- **A first-run wizard through school and carnival:** poor for bulk work and for setup spread over days. [ADR-0054](0054-setting-up-a-carnival.md) already chose a checklist.
- **The roster import as a school-setup step:** comes before the year is known, and is not the order the maintainer asked for.
- **A roster per carnival:** duplicates students, and supersedes [ADR-0060](0060-the-competition-year-is-chosen-at-import.md) §2 for nothing step 4 does not already give.
- **Copying a new carnival from the last one of its sport:** not now. No school has run a second season yet, and the standard events with the roster's ages rebuild a carnival in one press. Revisit after a school's second season.
- **Opening divisions on the server whenever the carnival opens:** schools that open events as they are called need the carnival-only open ([ADR-0054](0054-setting-up-a-carnival.md)).
- **A server-side gate on opening:** a school may deliberately run on last year's roster. The check warns.
- **Keeping officials on the Deck with a better field:** assignment is planning done before the day, not capture.
