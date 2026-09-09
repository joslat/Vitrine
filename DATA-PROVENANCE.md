<!-- SPDX-License-Identifier: MIT -->
# Data provenance

VITRINE uses synthetic data only:

- fictitious personas and shopping histories;
- invented catalogue products, identifiers, prices, stock states, reviews, and departments;
- authored shopping queries, expected behavior, evaluation criteria, attacks, and ablations;
- locally generated deterministic and model-backed observations of the synthetic subject.

No Digitec Galaxus production data, customer information, private catalogue, internal prompt,
system instruction, endpoint, API key, or operational telemetry is included.

The repository includes a bounded historical evidence receipt at
`docs/evidence/vitrine-synthetic-live-2026-09-04-eval02b-02c.html`. It reports genuine observations
of an earlier synthetic sample, not a measurement of a company system. Provider prompts,
responses, credentials, and infrastructure identifiers are deliberately excluded.

Generated local run evidence is written below `.agenteval/` and is ignored by Git. Committed report
fixtures use portable repository-relative paths and contain no live secrets.
