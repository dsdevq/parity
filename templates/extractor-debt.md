# Extractor debt - <Module>

Raw designer statements and diagnostics the extractor could not interpret for this module. Each line
carries the location or diagnostic code exactly as printed, an owner, and a date by which it is either
fixed in parity (issue filed) or accepted. The gate refuses an unacknowledged entry.

- Forms/<Form>.Designer.cs:<line> - <what the statement does> - owner <name> - until 2026-12-31 - parity issue #<n>
- TYPE-UNRESOLVED: <Form>.<member> - <why> - owner <name> - until 2026-12-31 - add the reference to extract.config.json
- UNREACHED-METHOD: <Form>.<method> - <dead code | wired in X> - owner <name> - until 2026-12-31

<!-- The lint prints the exact key it expects in its COV-RAW message: copy it. A bare code on a line acknowledges nothing. -->
