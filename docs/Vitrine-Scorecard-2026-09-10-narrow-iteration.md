<!-- SPDX-License-Identifier: MIT -->

# VITRINE evaluation — 2026-09-10 narrow iteration

- Scored commit: `f58a747bbe47fad0ec719c52add29f3ae4790dec`
- Implementation commit: `028ac51bd73987b6c4292e78408a9b56de560fe2`
- Branch: `review/high-leverage-v2`
- Reviewer: Codex self-review, reconciled with separate technical/architecture and
  product/presentation/documentation scoring lanes plus a bounded adversarial code review. This is
  not an independent human, recruiter, or commerce-team assessment.
- Scope: the checked-in portfolio sample, CLI, Avalonia app, offline and paid-evaluation contracts,
  current public documentation, and the sanitized live receipt published by the scored commit.
- Executed paths: zero-warning Release build; complete `Category!=LiveModel` lane; full offline
  evaluation and mutation panel; exporter failure/rollback self-test; documentation and publication
  verifiers; and exactly one paid Sofia Eval02 workflow observation. The paid run was not repeated
  to seek a more flattering result.
- Explicit exclusions: no production or representative customer data, commercial-impact
  measurement, independent usability review, human judge calibration, repeated live reliability
  study, live recovery/fallback exercise, provider-backed Entra/managed-identity exercise, or live
  use of a judge deployment distinct from the subject.
- Evidence: 0-warning/0-error Release build; 449/449 credential-free tests; 5/5 mandatory offline
  gates plus one passing diagnostic; 43/43 registered mutation detections; 29 verified
  Markdown/HTML files; 260 deny-by-default classified paths; and one strict sanitized Eval02 receipt
  bound to the implementation commit. That receipt records one measured workflow trial, all four
  authored criteria met, all three schema-required provider stages—InterestMapper, Ranker, and
  Presenter—completed, and no unusable, failed, cancelled, or terminal provider stage. The 29/260
  figures describe scored revision `f58a747`; the later archival commit that adds this scorecard
  makes the final repository totals 30/261.

The method is unchanged from the baseline: rubric version 1.0, the same six dimensions, weights,
anchors, evidence ceilings, and uncertainty aggregation. The immutable baseline scorecard was not
rewritten. The weighted result is
`Σ(score × weight percentage) / 100 = 8.505`, rounded once to **8.5/10**. The range is a rubric
review range, not a statistical confidence interval.

| Dimension | Weight | Score | Uncertainty | Direct evidence | Limiting fact |
|---|---:|---:|---:|---|---|
| Technical accuracy | 20% | 8.6 | ±0.4 | Typed workflow attempt identity and `Completed`/`Recovered`/`FinalFallback`/`Unrecovered`/`Cancelled` states now agree across runtime, normalizer, app schema 10, and exporter. The commit-bound live receipt is internally consistent. | The one live observation exercised only clean completion. Commit binding is operator-supplied plus checksummed, not a signature. |
| AI engineering | 20% | 8.5 | ±0.4 | Central Azure OpenAI composition serves subjects, workflow, judge, safety, and embeddings; identity modes never fall back to keys; subject/judge deployments are separately configurable; structured-response and malformed-evidence paths fail closed. | The live receipt used the same model for subject and judge. Separate-judge and Entra/managed-identity paths are source/test verified but not provider-executed; one trial is not reliability. |
| E-commerce relevance | 15% | 8.3 | ±0.7 | The live receipt records the judge marking capability-gap, lane-separation, owned-durable, and grounded-advice criteria met; `GapsUnresolvable` discloses incomplete coverage. The other cases still cover cross-category intent, gifts, compatibility, ownership, and abstention. | Catalogue, personas, prices, stock, and outcomes are synthetic. No commerce-team review or business KPI exists. |
| Recruiter first impression | 15% | 8.1 | ±0.7 | The customer-first story, bounded Agentic RAG visual, simpler first-view eval selection, canonical setup guide, and linked current receipt create a short inspectable proof path. | The control room and evidence vocabulary remain dense, and no independent reader/usability observation exists. |
| Pragmatic architecture | 15% | 8.5 | ±0.4 | CLI, app, and eval adapters retain shared subject cores; provider construction has one owner; the 738-line runtime guardrail harness was removed after its twelve distinct checks became named regressions; no parallel event stream was added. | Evidence invariants are necessarily repeated at runtime normalization, app serialization, and PowerShell publication boundaries; configuration remains environment/static rather than application DI. |
| Documentation credibility | 15% | 9.0 | ±0.4 | The canonical live guide, schema-10/1.4 contract, recovered-versus-terminal semantics, separate-judge option, exact commands, safe receipt workflow, 29-file link verifier, and commit-bound result agree. | Identity and separate-judge routes remain unexecuted live, and the judge has no independent human calibration. |
| **Weighted total** | **100%** | **8.5/10** | **8.0–9.0** | Two internal scoring lanes independently applied rubric v1.0 before reconciliation. | The +0.4 rounded movement is inside the overlapping review bands and is directional, not an independently measured quality gain. |

## Claim and evidence conflicts

- No conflict was found between the current typed projections, app integration tests, and sanitized
  receipt; the live UI was not separately exercised in this iteration. Attempt-level failures
  remain visible as reliability evidence. A recovered stage remains measurable; an unrecovered,
  cancelled, or final-fallback stage invalidates quality measurement.
- `Planned subject executions = 1` is a logical trial count, not a promise of one low-level model
  request. The receipt separately records the InterestMapper, Ranker, and Presenter attempts.
- The paid receipt says `sameModel`: separate judge routing exists and is tested, but was not used in
  this observation.
- The single Sofia pass proves that one configured end-to-end path worked once. It does not prove
  generalization, reliability, an agent-versus-workflow winner, or commercial impact.

## Change from the baseline review

- Previous scored commit and total: `67ab7375453a5ce975b88076fb75959afa00a9ed` — **8.1/10**
  with review range **7.6–8.6**.
- Current scored commit and total: `f58a747bbe47fad0ec719c52add29f3ae4790dec` — **8.5/10**
  with review range **8.0–9.0**.
- Rounded directional delta: **+0.4**. The ranges overlap, so this is not a meaningful measured
  improvement claim.
- No dimension decreased under the fixed rubric. E-commerce relevance stayed at 8.3 because new
  engineering evidence does not turn synthetic cases into business evidence. The largest direct
  gains came from fixing the baseline retry-classification defect, centralizing provider/auth
  composition, deleting the legacy runtime harness, and publishing a current sanitized receipt.

## Decision

**Ship this branch and stop adding portfolio features.** The narrow iteration removed the three
specific baseline limitations it targeted. More agents, personas, eval plans, or diagrams would add
surface area faster than credibility. Continue only with external or repeated evidence work when
that evidence is worth the time and provider cost.

## Highest-impact next iteration

1. Build a small blinded, human-reviewed criterion set; measure judge agreement and run the judge
   on a genuinely separate deployment — technical accuracy, AI engineering, and documentation
   credibility — high evidence gain, medium effort and review cost.
2. Pre-register and run a locked, repeated all-scenario live reliability study with the existing
   whole-trial acceptance rule and full failure census — AI engineering, technical accuracy, and
   e-commerce relevance — high evidence gain, medium provider cost; do not rerun until green.
3. Ask independent recruiter and commerce reviewers to perform the 60-second and 10-minute paths;
   record completion, misunderstandings, and relevance feedback, then remove or rewrite only what
   the observations justify — recruiter first impression and e-commerce relevance — high
   uncertainty reduction, low code risk.

An Entra/managed-identity provider smoke is the next operational check after these three if that
authentication route matches the intended hosting environment; it should validate the existing
path, not introduce another product surface.
