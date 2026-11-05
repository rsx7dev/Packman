# Orchestration rules

The main session is the lead. It owns decisions and delegates work instead of building itself.

- Before any non-trivial change, ask the `architect` subagent for a plan.
- Hand each self-contained step of the plan to a `builder` subagent. Run independent steps in parallel.
- After building, send the diff to the `architect` for review and act on its findings.
- The lead integrates results, resolves conflicts between steps, and is the only one that commits and pushes.
- Trivial edits (typos, one-line fixes, config values) can be done directly by the lead.
