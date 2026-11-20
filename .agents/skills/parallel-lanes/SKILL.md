---
name: parallel-lanes
description: "Implement an OpenSpec change in parallel lanes: the serial part first, then one branch and worktree per lane, and the merge procedure that lets lane agents merge into the feature branch without merging over each other. Use when you implement a change whose tasks.md recommends a fan-out, or when you are a lane agent."
---

# Parallel lanes

Every `tasks.md` ends with a "Dependencies and parallel work" section. It names the serial part, the lanes and the point where the lanes can start (the fan-out point). This skill is the procedure for that section. Most changes do not fan out. A lane is worth its cost only when it owns enough work, because each lane needs its own worktree, an `npm ci`, a fresh build and a fresh read of the change.

## Models

Use AGENTS.md "Model tiers". The serial part runs on the strong tier with high effort, because it lands the shared contracts (API routes and DTOs, database migrations, shared app types). A lane runs on the standard tier when its tasks are precise and the contract it builds on is committed. Otherwise it runs on the strong tier. A standard-tier lane starts at the default effort. Raise it to high when a review finds skipped files or tests.

## Procedure

1. **Serial part.** One agent implements the serial part on the feature branch and commits it. It reports a short summary of the contracts that the lanes build on.
2. **Fan-out.** At the fan-out point, give each lane its own branch off the feature branch (`<feature>-<lane>`) and its own worktree next to the main checkout (`git worktree add ../carnival-app-<lane> <feature>-<lane>`). Each lane starts from the committed state and the contract summary, never from guessed interfaces. Each lane agent edits only the files its lane owns. The lane brief carries the writing rule of AGENTS.md "Writing", including the Australian spelling, because the simple-english skill says American spelling.
3. **Lane commit.** The lane agent commits its lane on its own lane branch. It does not tick `tasks.md`. It reports the IDs of the tasks it finished, and only the main session ticks them. Then the lane agent stops until the maintainer approves the lane (the **staged-implementation** skill) and its review fixes are committed.
4. **Catch up.** In the lane worktree, merge the current feature branch into the lane and resolve conflicts. Build, and run the lane's tests plus the tests of the areas that other merged lanes touched.
5. **Merge under a lock.** Take the feature's merge lock: `flock "$(git rev-parse --git-common-dir)/<feature>.merge.lock" <command>`. The lock file is inside `.git`, so it is never an untracked file. Make sure that the feature worktree is clean, then run `git merge --ff-only <lane>` there. If the feature branch moved in the meantime, release the lock and go back to step 4.
6. **After the merge.** Build in the feature worktree, release the lock and report the merge commit.

Lanes do not push. The main session reviews every merged lane, runs the integration group, and removes the lane worktrees afterwards (`git worktree remove`; the branches are kept).
