---
name: staged-implementation
description: "Implement a plan stage by stage: one stage (task group or lane) at a time in a background agent, then one review, a short explanation and a guided review with the maintainer before the next stage starts. Parallel lanes are built in parallel and reviewed one after another. Use when you implement an OpenSpec change (openspec-apply-change) or any plan that has stages."
---

# Staged implementation

A **stage** is one task group of a `tasks.md`, or one lane of a fan-out (see the **parallel-lanes** skill). The maintainer reviews every stage before the next one starts. Reason: problems are found while the stage is still small, and the main session stays a coordinator with a small context.

## Who does what

- A background agent implements the stage. Use the model and effort that the "Models" table in `tasks.md` gives for the stage (AGENTS.md "Model tiers"). Give the agent a self-contained brief: the change folder, the stage, the files it owns, and the AGENTS.md rules that apply (hard rules, comments, docs tree, no prettier). The brief also carries the writing rule of AGENTS.md "Writing", including the Australian spelling, because the simple-english skill says American spelling.
- The agent implements one stage, runs the tests that are relevant to it, commits on the feature branch, reports and stops. It does not start the next stage. Its report is short: the files that changed, the tests it ran with their results, where it deviated from the plan and why, and what is still open.
- Planning, decisions, the guided review and the discussion stay in the main session. The main session does only small edits itself (a few lines in one file).

## After each stage

1. **Full suites.** The stage agent runs the full suites once for the stage, before it reports: `cd api && dotnet test` and `cd app && npm test`. It gives the results in its report.
2. **One review.** Give the stage's diff to one independent reviewer agent on the strong tier. The reviewer gets the diff and the rules, not the builder's reasoning, and looks for defects. Scoring, result lifecycle, authorisation and audit work uses high effort.
3. **Explanation.** Tell the maintainer in a few sentences what the stage did and why, which decisions the agent made, and where it deviated from the plan.
4. **Guided review.** Walk the maintainer through the diff:
   - the order to read the files in, with links to files and lines;
   - what to skim (mechanical or generated changes);
   - where to look closely (shared contracts, the result lifecycle, authorisation, risky logic), and the question to ask there;
   - the review findings, each with the fix you propose; low-severity findings are batched into one fix or added to `docs/BACKLOG.md`;
   - how the stage was tested, and how the maintainer can try it. For UI, show the screenshots from `npm run screenshots`.
5. **Decision.** Ask the maintainer to approve the stage or to ask for changes. Fix the findings and the requests in one follow-up commit on the stage's branch, and show the fixes in short. Do not review the fix commit again unless it changes logic.
6. **Next stage.** Start the next stage only after the maintainer approves this one. Continue the same agent when it still has useful context. Otherwise start a new one.

Keep the explanation and the guided review short: they cover one stage, not the whole change. The pull request description at the end follows the **pr-description** skill.

## Parallel lanes

- Lanes are built at the same time, each in its own worktree (the **parallel-lanes** skill).
- A lane agent commits its lane, reports and stops **before** it merges into the feature branch.
- The main session reviews the lanes one after another, in the order of `tasks.md`. A lane that is not finished yet is skipped and reviewed when it finishes. Each lane gets its own review, explanation, guided review and decision (steps 2 to 5).
- After the maintainer approves a lane, its fixes are committed in the lane. Then it merges into the feature branch by the parallel-lanes merge procedure. Then the next lane is reviewed. Its merge brings in the lanes that were approved before it.
- The integration group after the lanes is one more stage, with its own review.
