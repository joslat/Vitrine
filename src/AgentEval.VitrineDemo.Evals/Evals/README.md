<!-- SPDX-License-Identifier: MIT -->
# Evaluation implementations

This directory separates evaluation behavior by execution cost and subject boundary:

- `Offline/` contains the deterministic, credential-free `EvaluationSuite`, production and
  recommendation checks, benchmark arms, self-test, and `Diagnostics/` negative-control fixtures.
- `Live/` contains the explicitly paid Eval 01 agent, Eval 02 workflow, Eval 03 comparison,
  Eval 04 stochastic-agent, Eval 05 stochastic-workflow, and Eval 06 Robin safety plans; the four
  canonical use-case ground-truth/criteria/tool-expectation mappings; typed
  subject/tool/workflow/probe evidence; reliability/comparison projection; and the sanitized live
  session store.

Shared CLI orchestration, progress/result contracts, report projection, and the process entry
point remain at the project root because both lanes use them. `EvaluationExecutionProfile` is now
strictly offline: `OfflineDeterministic` is its sole value. The old live profile member was
removed; `--live-subjects-and-judge` is only a `--confirm-paid`-gated alias for Eval 03.

The strict offline suite remains the default and retains its regression, report, self-test, and
43-control value. Selecting a live plan must be explicit and paid execution must be confirmed. If
provider configuration is missing, readiness starts no paid subject and never substitutes the
offline suite. Eval 01–05 write one standard AgentEval run directory per arm/repetition below
`.agenteval/live`; Eval 06 uses `RedTeamRunner` directly and writes no benchmark run directory.
Every live session, including a readiness failure, writes a sanitized
`live-sessions/<session-id>/outcome.json` and updates `live-sessions/index.json`.

The Eval 01–05 result reports its quality pass threshold (`0.75` by default), bounded criterion
explanations, per-check census/Wilson facts, and—on Eval 03—the native paired-case
`RepCollapse.All` comparison context including repetition counts and minimum attainable p.
Terminal provider failure/cancellation is `NotMeasured`. Demo02 may separately use its bounded,
disclosed mapper/reviewer/ranker/presenter fallbacks after two unusable structured-output attempts;
degradation count and allow-listed kinds are retained, and a terminal provider failure remains
`NotMeasured` even if a fallback response exists.

Eval 06 is fixed rather than scenario-selectable: fresh real Robin targets receive AgentEval
`JailbreakAttack` and canary-backed `SystemPromptExtractionAttack` probes. The default is two
probes per attack, with a declared ceiling of 25 target model calls plus at most one fallback-judge
call per probe (104 model calls for the four-probe campaign). It persists only redacted
attack/probe census and allow-listed metadata; raw prompts, responses, reasons, and the canary are
excluded. The use-case `0.75` threshold does not classify this safety plan.

The stable programmatic selector is `LiveEvaluationPlanRunner`. The named convenience entry
points are `Eval01_Agent`, `Eval02_Workflow`, `Eval03_Comparison`,
`Eval04_StochasticAgent`, and `Eval05_StochasticWorkflow`; each delegates to the same validated
runner and typed result contract. `Eval06_SafetyProbes` delegates to the same paid-plan boundary
but executes the released AgentEval red-team runner rather than a use-case benchmark.
