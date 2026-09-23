CTRF schema vendored from ctrf-io/ctrf at
`f76b0b11e79a7f58edf8c63a3a26316a0da96177`, `static/ctrf-schema.json`, with one
local change (ADR-021): a test's `suite` may be a string, as xUnit 3.x writes it,
or a non-empty array of strings, as xUnit 4.x writes it and as later upstream
revisions define it (`schema/ctrf.schema.json`). Those revisions accept only the
array, so a newer pin would reject every xUnit 3.x report; the pin is patched
instead. Validate updates against the real xUnit fixtures of both versions before
changing this pin. The upstream MIT license is included.
