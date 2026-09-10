<!-- SPDX-License-Identifier: MIT -->
# Data provenance

VITRINE uses authored/synthetic commerce records:

- fictitious personas and shopping histories;
- authored catalogue identifiers, prices, stock states, reviews, departments, and relationships;
- recognizable third-party product, brand, and model names used illustratively; their associated
  catalogue records are synthetic and are not copied from a retailer's production catalogue;
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
