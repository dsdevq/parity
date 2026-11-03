---
name: parity-record
description: Help a person export one scenario they just ran in the legacy client from the host's per-call log into a sealed, redacted recording, the oracle the new client is diffed against. Use when asked to "record <scenario>", "export the recording", or to change normalization rules.
---

# parity-record

A person-led phase. The person runs the scenario once in the legacy client with the host's per-call log on;
you help turn that run into `legacy/<Module>/recordings/<scenario>/`. You never run the legacy client and you
never invent a value. This is the only phase in which `recordings/` (the recordings and `normalization.json`)
may change; the guard refuses it everywhere else. The reference is docs/oracle.md in the parity repository.

1. Ensure `legacy/.phase` contains `record`. The scenario must already be in `legacy/<Module>/registry/scenarios.json`
   (written in the map phase); the export refuses one that is not.
2. Ask the person for: the dump of the host log rows of their run (JSON lines, one row per call, columns
   `time`, `session`, `category`, `type`, `request`, `reply`, `dataset`, `error`, `durationMs`; docs/oracle.md in the parity repository, The
   host log), the session id or the time window of the run, their name, the environment and the legacy build.
   Keep the dump outside the repo: it holds real data before redaction.
3. Check `oracle.redact` and `oracle.writes` in `legacy/extract.config.json` with the person: every field that carries a
   person, an id or an amount needs a redaction rule, and every call that changes host state is a write.
   `PARITY_REDACT_KEY` must be set (the same private value wherever recordings and replays are exported).
4. Run `"${PARITY_BIN:-parity}" oracle export legacy/<Module> scn:<id> --log <dump> --session <id> --recorded-by <name> --environment <env> --legacy-build <build>`.
   It prints the folder, the call count, the writes, how many values it redacted and the seal.
5. Read every file it wrote with the person. A value that is still real data means a redaction rule is missing:
   add it, then export again with `--replace`. Commit the recording only when the person agrees nothing real is left.
6. Normalization rules (`legacy/<Module>/recordings/normalization.json`, from `legacy/templates/normalization.json`)
   hide values that differ on every run (a session id, a timestamp, a generated key). Each rule names a `dec:` entry in
   `legacy/<Module>/registry/decisions.json` that cites the rule, with a reason the person can judge without domain knowledge ("the
   value is different every session"). Write the decision from what the person said; never answer for them. Raise
   `version` with every change. A rule that hides a business value (a date the client chose, an amount) is not
   normalization: it is a difference to fix.

Stop. Report the recording's folder, its call count, the redaction rules applied, and the questions only the person
can answer. Replaying and fixing the new client happen in the implement phase, with oracle verify.
