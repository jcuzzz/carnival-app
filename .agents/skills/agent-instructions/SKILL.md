---
name: agent-instructions
description: Rules for agent instruction files and skills in this repository (AGENTS.md, CLAUDE.md, .agents/skills, .claude/skills, .claude/agents, openspec/config.yaml). Use before you add, change, move or split an AGENTS.md or CLAUDE.md, or add or change a skill.
---

# Agent instruction files and skills

This repository keeps its agent rules in `AGENTS.md` and its skills in `.agents/skills/`. Claude Code details go only in `CLAUDE.md`, in the stubs in `.claude/skills/` and in `.claude/agents/`. All tools then read the same rules. Reason: contributors use different AI tools, and each one must follow the same rules.

## Keep AGENTS.md small

Codex reads the `AGENTS.md` files from the repository root down to the folder it was started in. It stops at 32 KiB in total, and a contributor's global file counts too. Long files also cost context in every tool, on every task. Put each rule in the place where it is read only when it is needed:

1. Put a rule in the root `AGENTS.md` only when almost every task needs it.
2. Put rules for one folder (for example `api/` or `app/`) in that folder's `AGENTS.md`, with a `CLAUDE.md` next to it that contains only `@AGENTS.md`. Link it from the root `AGENTS.md`, so that a tool that does not load nested files still finds it.
3. Put a procedure that only some tasks need (a release, a merge, a review) in a skill.
4. Put the method behind the rules in `docs/development/agent-practices.md`, and reference material in the docs tree. Link to it.
5. Before you remove a rule, make sure that its reason is gone. Write the reason next to each new rule, so that the next person can make that decision.

Keep the root `AGENTS.md` and any nested `AGENTS.md` under 24 KiB together. This leaves 8 KiB for a contributor's global file.

**Never compress to meet the limit.** When a file is too large, move content to the place where it is read (steps 1 to 4). Do not shorten sentences, drop details or merge rules to win bytes. Rewording that keeps every fact is fine. Removing a fact is a rule change and needs the reason from step 5. Before you commit a change to an `AGENTS.md`, compare the removed lines with the added ones: every fact that leaves must appear in the file it moved to, or the commit message names it as removed on purpose, with the reason.

## Skills

Skills follow the open `SKILL.md` format and live in `.agents/skills/<name>/`, the shared location that Codex, Cursor, Gemini CLI and other tools read. Claude Code reads only `.claude/skills/`, so each skill there is a stub:

- **Own skills:** write the skill and its files in `.agents/skills/<name>/`. Add `.claude/skills/<name>/SKILL.md` with the same frontmatter (`name`, `description`, and `disable-model-invocation` when the skill has it) and one line: "Read `.agents/skills/<name>/SKILL.md` and follow it." Use stubs, not symlinks, because Git for Windows checks symlinks out as plain text files by default.
- **OpenSpec skills** are generated. `openspec init --tools agents,claude`, or `openspec update` after a CLI upgrade, writes them to `.agents/skills/openspec-*/`. Do not edit them by hand. The CLI links new ones from `.claude/skills/` with symlinks. Replace each new symlink with a stub. `openspec update` keeps existing stubs and refreshes only the `.agents/` copies.
- **Project rules for OpenSpec** go in `openspec/config.yaml` (`context`, `rules` for each artifact, `operations` guidance), not in the generated skills. The skills read that file each time.
- Write skills tool-neutral: no tool names like a specific question popup and no model names. Say what to do ("ask the questions in one message", "use a search agent on the small tier") and let the tool pick its mechanism. Claude Code mechanisms go in `CLAUDE.md`.
- A skill copied from another source keeps its license file and names its source in `metadata.source`.

## Subagent definitions

`.claude/agents/<name>.md` defines a Claude Code subagent. Give each one an explicit `model`. A definition with the name of a built-in agent (for example `Explore`) replaces the built-in one.
