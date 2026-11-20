@AGENTS.md

## Claude Code specifics

- Model tiers (AGENTS.md "Model tiers"): frontier = `fable`, strong = `opus`, standard = `sonnet`, small = `haiku`. Pass `model` on every Agent call, and `effort` where the tasks.md Models table asks for high effort. A call without `model` runs on the main session's model.
- `.claude/agents/Explore.md` replaces the built-in Explore agent with a read-only search agent on `haiku`, because the built-in one runs on the main session's model. The `grilling` skill uses it for fact lookups across many files.
- Grilling questions go in one message as numbered text rounds (the `grilling` skill format), not in a question popup.
- Skills in `.claude/skills/` are stubs that point to `.agents/skills/`. Add new skills by the `agent-instructions` skill.
- New preferences go into `AGENTS.md` (Working preferences), not here, unless they apply only to Claude Code.
