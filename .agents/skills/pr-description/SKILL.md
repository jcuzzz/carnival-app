---
name: pr-description
description: "Write the description of a new pull request on GitHub (what and why, decisions, a reading guide, where to look closely, how it was tested), give a context comment to each changed file whose purpose the reading guide does not make clear, and add the few line comments that help a reviewer. Use when you open a pull request."
---

# Pull request description and author comments

A reviewer needs to know why the change exists, where to start reading and where to look closely. The description gives this. Author comments on files and lines are few. They give only what the diff and the reading guide cannot say. Anything that a later reader of the code needs goes into the code, the commit message, the docs tree or the OpenSpec design, not into the pull request, because nobody reads the pull request after the merge.

Reason: a comment on every file repeats the diff in a large pull request (API, app, tests and docs together), and the reviewer does not use it. A short description with a reading guide and a few comments is what review guides recommend (see Sources).

## The description

Write these parts, in this order. Keep each one short.

1. **What and why.** Two or three sentences: the problem, and what the change does about it. Link the OpenSpec change folder and the `docs/BACKLOG.md` item, if there is one.
2. **Decisions.** The choices a reviewer can question, each with its reason and the alternative that was not taken. Link the ADR and the OpenSpec design for the rest. The description must make sense without the links.
3. **Reading guide.** Where to start (the two or three files with the core change), and what to skim. Name the mechanical parts and say what they do, for example "the 12 test files only rename `CarnivalDto` to `CarnivalSummary`".
4. **Look closely at.** The places where a mistake is easy to make and hard to see, for example "the outbox must keep the client's UUIDv7 key when it retries".
5. **Testing.** What ran (`dotnet test`, `npm test`, `npm run test:e2e`, screenshots, a fresh-install walk-through, a deploy to a test host), and what was not tested and why.

If a pull request holds more than one change, give each change its own short section with parts 1 and 2. Keep one reading guide for the whole pull request.

## File context comments

Give a changed file one file-level comment only when the reading guide does not make its purpose clear. Typical cases are a new file whose role is not obvious from its name, or a changed file whose change has a reason that is not in the diff. Skip docs, generated files, lock files and files that the reading guide already names as mechanical.

- **A new file:** what it is for, how it fits with the files around it, and the one or two decisions that shaped it.
- **A changed file:** what changed at a high level and why.
- **A test file:** what it checks, and with which test data. One to three sentences.

Keep it to two to five sentences, with no label in front. Do not repeat the code comments and do not restate the diff ("added method X").

Post a file-level comment with the GitHub API, on the head commit of the pull request:

```bash
gh api repos/{owner}/{repo}/pulls/<number>/comments \
  -f commit_id="$(gh pr view <number> --json headRefOid -q .headRefOid)" \
  -f path="<file>" -f subject_type=file -f body="<comment>"
```

## Author comments on lines

Add a line comment only when it helps the reviewer and its content does not belong in the code. Most pull requests need between none and five. Post it like a file comment, with `-F line=<n> -f side=RIGHT` instead of `subject_type`. Start each comment with a label, so that the reviewer knows what you expect:

- `Note:` context that only matters for this review. Example: "Note: this is the fix. The rest of the file is the rename."
- `Please check:` a risky place that you want a second look at.
- `Question:` something you are not sure of and want the reviewer to decide.

Do not write these comments:

- A comment that says what the diff already shows ("X changed to Y", "new method Foo").
- An explanation of code. If the code needs one, add a code comment or make the code clearer. A later reader then gets it too.
- A reason that stays true after the merge. Put it in the commit message, the ADR or the design, and link it.

Open the pull request with `gh pr create --base main --title "<title>" --body-file <file>`. Post the description and the comments directly, without a preview. The maintainer reviews them on GitHub. When you report the new pull request, give its full URL.

## Sources

- Google engineering practices: [CL descriptions](https://google.github.io/eng-practices/review/developer/cl-descriptions.html) and [handling reviewer comments](https://google.github.io/eng-practices/review/developer/handling-comments.html).
- GitHub: [helping others review your changes](https://docs.github.com/en/enterprise-server@3.15/pull-requests/collaborating-with-pull-requests/getting-started/helping-others-review-your-changes).
- Jake Worth: [six tips for reviewing your own code](https://jakeworth.com/posts/six-tips-for-reviewing-your-own-code/).
