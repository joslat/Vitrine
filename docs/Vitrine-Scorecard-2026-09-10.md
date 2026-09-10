<!-- SPDX-License-Identifier: MIT -->

# VITRINE evaluation — 2026-09-10

- Commit: `67ab7375453a5ce975b88076fb75959afa00a9ed`
- Branch: `review/high-leverage-v2`
- Reviewer: Codex self-review, reconciled with three separate internal review lanes. This is not an
  independent human or commerce-team assessment.
- Scope: the checked-in portfolio sample, CLI, Avalonia app, offline and paid-evaluation contracts,
  generated public evidence, and operator documentation.
- Executed paths: Release build; the complete `Category!=LiveModel` lane; offline evaluation and
  mutation evidence; one paid Demo01 Nadia CLI run; one paid Demo01 Nadia app-coordinator run; one
  paid Eval02 Sofia run; and headless inspection of the evaluation board and Demo02 graph.
- Explicit exclusions: no production or representative customer data, business-impact measurement,
  independent usability review, repeated live reliability study, current fully measured workflow
  judge verdict, Eval03–Eval06 paid run, or claim that one live scenario generalizes.
- Evidence: 0-warning/0-error Release build; 407/407 credential-free tests; 5/5 mandatory offline
  gates plus one reported diagnostic; 43/43 registered mutation detections; 27 verified documents
  in the scored implementation revision;
  255 classified implementation-revision paths; successful live Demo01 CLI and app-coordinator
  observations; and a correctly retained `NOT MEASURED` Eval02 observation after one Ranker request
  failed and its retry recovered. Raw paid-run material remains local and untracked.

The scoring method, anchors, ceilings, formula, and uncertainty calculation are fixed in
[SCORING-RUBRIC.md](SCORING-RUBRIC.md). The weighted result is
`Σ(score × weight percentage) / 100 = 8.115`, rounded once to **8.1/10**. The range is a rubric
review range, not a statistical confidence interval.

| Dimension | Weight | Score | Uncertainty | Direct evidence | Limiting fact |
|---|---:|---:|---:|---|---|
| Technical accuracy | 20% | 7.9 | ±0.4 | Typed missingness, exit authority, diagnostic colours, event chronology, accurate live HTML usage, and 407 tests agree. | A recovered workflow request is still classified by `HasWorkflowProviderTerminalFailure` as a terminal provider failure. |
| AI engineering | 20% | 8.0 | ±0.4 | Shared live subject cores, bounded tools/rounds/timeouts, structured output, grounding, post-screening, negative controls, paid confirmation, sanitized receipts, and two successful Demo01 live adapters. | The current workflow eval is not measured; subject and judge share one deployment; authentication is API-key-only; one live case proves connectivity, not reliability. |
| E-commerce relevance | 15% | 8.3 | ±0.7 | Nadia, Sofia, Marco, and Luca test cross-category discovery, replenishment versus durable capability, gift contamination, compatibility/ownership, and thin-signal abstention. | All personas, catalogue records, prices, stock, and outcomes are synthetic; no commercial KPI is measured. |
| Recruiter first impression | 15% | 8.0 | ±0.7 | Customer output leads, the 60-second route is explicit, advanced eval plans are hidden initially, and the bounded Agentic RAG visual explains the maker–critic loop. | The control room and evidence language remain dense, and no independent reader/usability observation exists. |
| Pragmatic architecture | 15% | 7.9 | ±0.4 | CLI, app, and eval adapters genuinely converge on `RecommendationRunEngine` and `GalaxusDiscoveryLoop`; typed seams, render-only graph grouping, shared replenishment logic, and dead-code removal reduce drift. | The legacy Demo01 guardrail harness still runs automatically, and provider-attempt versus final-stage outcome is not modeled explicitly. |
| Documentation credibility | 15% | 8.7 | ±0.4 | One canonical live guide covers endpoint type, four variables, deployment names, API-key limitation, paid smokes, embedding cost, safe evidence, and troubleshooting; all 27 documents in the scored commit verify. | The latest paid observations are deliberately untracked rather than a commit-bound sanitized public receipt. |
| **Weighted total** | **100%** | **8.1/10** | **7.6–8.6** | Three internal review lanes scored 8.0, 8.1, and 8.1 before reconciliation. | Small movements inside this band are directional, not meaningful evidence of improvement. |

## Claim and evidence conflicts

- The Eval02 console showed a mechanically completed five-executor workflow, while its aggregate was
  `NOT MEASURED`. The aggregate currently wins: one Ranker provider attempt failed, its retry
  recovered, but the strict predicate invalidates the whole subject after any failed attempt. This
  is conservative, but “terminal failure” is not a precise description of the recovered execution.
- Agent versus workflow remains useful matched diagnostic evidence, not proof of an architectural
  winner. Eval03 therefore remains available behind **Advanced** rather than being deleted or used
  as the primary story.
- “Microsoft Foundry setup” currently means an Azure OpenAI inference/resource endpoint and API key
  consumed by `AzureOpenAIClient`; it does not mean a Foundry project client, Entra ID, or managed
  identity. The live guide states this limitation explicitly.

## Decision

**Continue for one tightly scoped evidence/reliability iteration, then ship and stop.** The current
revision is already a strong, publishable portfolio sample. Do not add personas, agents, eval plans,
or more diagrams. Additional breadth is now more likely to reduce clarity than increase credibility.

If there is no time for that narrow iteration, ship this revision as-is and describe Eval02 as
`NOT MEASURED`; do not rerun merely until a green result appears.

## Highest-impact next iteration

1. Model provider attempts separately from the final stage outcome. Preserve a failed attempt as
   visible reliability evidence, but invalidate quality only when the stage ultimately lacks a valid
   model result or screened answer. Then run exactly one Sofia Eval02 and publish its sanitized,
   commit-bound receipt whether it passes or fails quality. This is the largest technical-accuracy,
   AI-engineering, and evidence gain.
2. Centralize live client construction, support a separately configured judge deployment, and add
   Microsoft Entra/managed-identity authentication with API-key fallback. Calibrate the judge on a
   small human-reviewed criterion set before claiming stronger evaluation validity.
3. Move the unique legacy Demo01 guardrail checks into the registered eval/test layer, then remove
   their unconditional CLI runtime harness. Keep Eval03 as an advanced diagnostic; do not delete the
   controlled comparison evidence.

## Change from previous review

This is the first review produced with rubric version 1.0, so it is the comparison baseline. Future
reviews must create a new dated file and cite their exact commit; this file must not be rewritten to
match later code.
