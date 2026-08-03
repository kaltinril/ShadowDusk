# CI gate triggers — the `run-integration` label has never fired

**Status:** 🔵 Open. Found 2026-08-03 while cutting the 0.18.0 release. CI wiring only; no
compiler code, no emitted bytes, no target behaviour. Owner asked to revisit rather than fix
same-day, because the fix is entangled with a design question only the owner can settle.

**Deliberately its own document.** This is not Phase 51 scope (that phase is leftover scope from
archived phases) and not a bug-hunt finding. It is a standing CI-infrastructure defect found
during a release, and it will be closed by a CI change, not by a compiler change.

---

## What is wrong

`ci.yml`'s `Integration Tests` job and the whole of `pack-consume.yml` are gated on the
`run-integration` PR label. **Neither workflow lists `labeled` in its `pull_request` `types:`.**
GitHub therefore defaults both to `[opened, synchronize, reopened]`, and **adding the label fires
nothing.** The label is decorative on both workflows that consume it.

| Workflow | Label it consumes | `types:` includes `labeled`? | Adding the label |
|---|---|---|---|
| `validation-render.yml` | `run-validation-render` | ✅ yes | works |
| `wasm.yml` | `run-browser` | ✅ yes | works |
| `ci.yml` | `run-integration` | ❌ no | **does nothing** |
| `pack-consume.yml` | `run-integration` | ❌ no | **does nothing** |

Two of the four behave exactly as documented, which is what makes the other two easy to miss: the
mechanism visibly works, so the natural assumption is that it works everywhere.

## It was born broken, not regressed into

- `git log -S"labeled" -- .github/workflows/ci.yml` → **empty**.
- `git log -S"labeled" -- .github/workflows/pack-consume.yml` → **empty**.
- At `970a8bf` (2026-06-05), the commit that introduced the `run-integration` gate, `ci.yml`'s
  `pull_request:` block already had no `types:`.

So it has been inert for roughly two months, and every PR since that "opted in" by adding the
label got silently nothing.

**Explicitly NOT caused by the CodeQL permissions work.** That commit (`5f997f7`, the 18
`actions/missing-workflow-permissions` alerts) was purely additive: 54 insertions, **0 deletions**,
every added line a `permissions:` key, a `contents: read`, a blank, or a comment. It touched no
`on:`, `types:`, or `if:` line in any workflow. This was checked because it was the obvious
suspect, and it is not the cause.

## The sharper half: `pack-consume.yml` has no standing trigger

Its own header comment admits it: unlike `ci.yml`'s integration job there is **no push-to-main
trigger**. Its only standing net is a weekly Monday 05:40 UTC cron. So the pack/consume path —
packing all eight NuGets and doing a cold consumer restore on 3 OSes × 2 TFMs — can be up to
**7 days stale at the moment of any release**, and neither `RELEASING.md` nor the `/release`
skill checks it.

For 0.18.0 this was closed by hand:

```bash
gh workflow run pack-consume.yml --ref version/0.18.0   # all 6 OS x TFM combos green
```

That manual step is written down nowhere as a release obligation, which is precisely the
"a check nobody remembers to run does not exist" failure mode from `CLAUDE.md` → *Handoff*.

## Fix, when picked up

Two independent decisions. Do not conflate them.

1. **Make the label actually work** (mechanical, two lines): add
   `types: [ opened, synchronize, reopened, labeled ]` to the `pull_request:` trigger of `ci.yml`
   and `pack-consume.yml`.

2. **Decide whether label-gating is the right model at all** (the owner's call). The owner's
   stated position on 2026-08-03 is that depending on a human to remember a label is the wrong
   design. Options, not mutually exclusive:
   - give `pack-consume.yml` a push-to-main trigger like the other heavy gates;
   - drop the gating entirely and accept the Actions minutes;
   - keep gating but have `/release` dispatch both workflows explicitly as a recorded step.

   If any gating survives in any form, `RELEASING.md` and the `/release` skill need a step for it.

> **Do not "fix" this by adding a reminder to a doc.** A reminder to remember is the same defect
> wearing a hat. Either the trigger fires on its own, or the release automation dispatches it.
