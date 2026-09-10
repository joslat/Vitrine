<!-- SPDX-License-Identifier: MIT -->

# VITRINE portfolio scoring rubric

Version 1.0. This is the stable procedure for scoring VITRINE after an implementation round. It
exists so two reviews use the same dimensions, evidence standard, weights, and interpretation.
It is a portfolio-review instrument, not a scientific measurement, hiring decision, production
certification, or substitute for user research.

## Procedure

1. Record the date, commit, branch, reviewer, and exact scope. Score the checked-out state, not
   planned work or uncommitted intentions.
2. Collect evidence before assigning numbers. Prefer, in order: current executed receipts and
   rendered UI; passing tests and inspected source; current documentation and diagrams; unsupported
   assertions. A higher tier does not make an irrelevant observation useful.
3. Score each of the six dimensions from 1.0 to 10.0 using the common anchors and
   dimension-specific questions below. Use one decimal only when the evidence supports that
   distinction.
4. Write at least one strength, one limiting fact, and one direct evidence reference for every
   dimension. A feature earns credit only where its behavior or communication is observable.
5. Apply the evidence and claim ceilings. Never average away a security, cost, measurement, or
   truthfulness defect that invalidates the defining claim.
6. Calculate the weighted total, then round once to one decimal. Enter weights as percentages, not
   whole-number multipliers:

   `total = Σ(dimension score × weight percentage) / 100`

7. Report uncertainty and conflicts of interest. A self-review is labelled as such and should be
   followed by an independent review before treating a small score movement as meaningful.
8. Finish with no more than three next-iteration changes, ordered by expected score/evidence gain
   per unit of complexity. Removing unsupported or duplicative surface area is a valid improvement.

## Common anchors

| Score | Meaning |
|---:|---|
| 1 | Absent, broken, or materially misleading. |
| 3 | Recognizable prototype, but major gaps prevent a credible demonstration. |
| 5 | Coherent demo with important unverified claims, rough edges, or narrow evidence. |
| 7 | Strong portfolio implementation: works on its declared path, is understandable, and has explicit limitations. |
| 8 | Production-minded sample with unusually good boundaries, tests, observability, and communication; remaining gaps are bounded. |
| 9 | Exceptional, evidence-backed work with only minor or deliberately scoped limitations. Another engineer can reproduce and challenge it. |
| 10 | Repeatedly and independently validated at representative production conditions with essentially no material gap in the dimension. Rarely appropriate for a synthetic portfolio sample. |

Scores 2, 4, and 6 interpolate between the adjacent anchors. A score above 9 is not “more
features”; it requires stronger and more independent evidence.

## Dimensions and weights

### 1. Technical accuracy — 20%

Judge whether implementation, UI, reports, and prose describe the same system and use terms
precisely. Inspect failure and missing-data semantics, statistics, state transitions, exit codes,
provider usage, model/deployment terminology, and whether warnings can be explained from typed
evidence. Penalize a green-looking child event that is presented as a green aggregate verdict,
silent fallback, stale counts, or documentation that promises unsupported behavior.

Key question: **Would a careful engineer reach the same conclusion after tracing the code and
receipt?**

### 2. AI engineering — 20%

Judge model/tool/workflow composition, retrieval grounding, structured-output handling,
guardrails, prompt and evaluator separation, deterministic and live lanes, evaluation design,
negative controls, observability, cost bounds, authentication, retry/timeout behavior, and durable
evidence. Reward measured limitations and explicit `NotMeasured` states. Do not award live-quality
credit for offline tests alone.

Key question: **Does the project make model behavior testable, bounded, attributable, and safe to
operate?**

### 3. E-commerce relevance — 15%

Judge whether the synthetic cases exercise meaningful commerce problems: cross-category intent,
replenishment versus durable capability, gift-history contamination, abstention from weak signals,
compatibility, ownership, catalogue authority, price/stock freshness, and customer evidence.
Separate an engineering hypothesis from measured commercial impact. Extra personas or products do
not earn credit unless they add a distinct decision or failure mode.

Key question: **Would a commerce team recognize valuable, realistic decisions here without being
asked to mistake synthetic data for business evidence?**

### 4. Recruiter first impression — 15%

Judge the first 60 seconds and first 10 minutes: clear purpose, visible customer outcome, ownership,
polish, navigation, setup friction, scannability, and a short proof path. Reward a coherent story
and working artifact. Penalize repeated caveats, mode overload, unexplained warning cards, and
architecture comparisons that dominate the value proposition without improving it.

Key question: **Can the intended reviewer quickly understand what was built, why it matters, and
where the proof is?**

### 5. Pragmatic architecture — 15%

Judge ownership boundaries, shared cores, typed contracts, dependency direction, test seams,
failure containment, operational simplicity, and deletion discipline. Reward one implementation
serving CLI, UI, and eval adapters when it truly does. Penalize dead code, duplicated orchestration,
decorative abstractions, and features whose maintenance cost is larger than their demonstrated
learning value. “Less is more” applies here explicitly.

Key question: **Is this the smallest architecture that preserves the demonstrated value and
evidence?**

### 6. Documentation credibility — 15%

Judge onboarding completeness, command correctness, link health, claim/source alignment,
terminology, safe live setup, troubleshooting, artifact interpretation, dates, and visible limits.
Reward one canonical owner for each contract with concise links elsewhere. Penalize duplicated
instructions that drift, secrets guidance that is unsafe, or historical measurements presented as
current evidence.

Key question: **Can another engineer reproduce the declared paths and know exactly what the result
does and does not prove?**

## Evidence and claim ceilings

These are ceilings on the affected claim or sub-area; apply them to the dimension as a whole only
when that claim defines the dimension.

- A path that neither builds nor has an inspected current receipt cannot receive more than 5 for
  “works end to end.” Source plausibility is not execution evidence.
- Configuration-presence checks establish only local readiness. Until a current live call succeeds,
  provider connectivity, authorization, deployment compatibility, and quota claims are capped at 4.
- One successful live scenario establishes one observation, not reliability or generalization.
  Reliability claims need repeated, fully measured trials with the declared statistical rule.
- Offline deterministic model boundaries can prove wiring, tools, workflow routes, and guardrails;
  they cannot prove live-model quality.
- An evaluator whose expected answer leaks into the subject, or whose observation supplies its own
  verdict, caps the affected evaluation-validity claim at 5 until independent ownership is restored.
- Missing measurements, terminal provider failures, and cancellations are never zeros or passes.
  Misclassifying one caps the affected technical-accuracy claim at 5.
- Implicit paid execution or unsafe secret handling caps the affected AI-operation claim at 5.
- Synthetic catalogue/persona evidence cannot substantiate conversion, revenue, margin,
  satisfaction, production latency, or company-system claims. Those business-impact claims remain
  at 3 or below without representative governed data.
- A checksummed artifact supports accidental-change detection, not author identity or resistance to
  malicious replacement.
- Self-review confidence is never “high” on presentation impact. Use an independent reader or
  usability observation to raise it.

## Uncertainty

Attach a range to every dimension, not just a point estimate:

- **±0.2:** multiple current, direct evidence sources agree and the path was executed.
- **±0.4:** good source/test evidence exists, but a relevant runtime or audience observation is
  missing.
- **±0.7 or more:** the judgment is mostly qualitative, historical, self-reviewed, or depends on a
  single narrow observation.

Calculate the overall review range with the same weights: clip each dimension's
`score − uncertainty` and `score + uncertainty` to 1–10, weight each endpoint with the formula
above, sum, and round the two final endpoints once to one decimal. This is a repeatable review
range, not a statistical confidence interval.

A change smaller than the larger of the before/after uncertainty bands is directional, not a
meaningful measured improvement. State disagreements between UI, logs, reports, and docs before
scoring; do not pick the most flattering surface.

## Review template

Copy this block for each scored revision.

```markdown
# VITRINE evaluation — YYYY-MM-DD

- Commit / branch:
- Reviewer (self or independent):
- Scope:
- Executed paths:
- Explicit exclusions:
- Evidence set (tests, receipts, UI observations, source/docs):

| Dimension | Weight | Score | Uncertainty | Direct evidence | Limiting fact |
|---|---:|---:|---:|---|---|
| Technical accuracy | 20% | — | — | — | — |
| AI engineering | 20% | — | — | — | — |
| E-commerce relevance | 15% | — | — | — | — |
| Recruiter first impression | 15% | — | — | — | — |
| Pragmatic architecture | 15% | — | — | — | — |
| Documentation credibility | 15% | — | — | — | — |
| **Weighted total** | **100%** | **—** | **report a range** | | |

## Claim/evidence conflicts

- None, or list each disagreement and which source currently wins.

## Change from previous review

- Previous commit and total:
- Directional delta:
- Evidence newly added or invalidated:

## Highest-impact next iteration

1. Change — affected dimensions — expected evidence gain — complexity/risk.
2. Change — affected dimensions — expected evidence gain — complexity/risk.
3. Change — affected dimensions — expected evidence gain — complexity/risk.
```

Store completed reviews next to this rubric or in a clearly linked evidence directory. Never edit
an old review to match a newer implementation; create a new dated review so score movement remains
auditable.
