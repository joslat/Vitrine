<!-- SPDX-License-Identifier: MIT -->
# Upstream provenance

- Application shell predecessor: [joslat/GatekeeperDemo](https://github.com/joslat/GatekeeperDemo)
  at commit `66f72c3e562bce5ae4f8ff9bf0ba81759ec35d29` (same author).
- Subject starting point: the `samples/Galaxus.RecommendationAgent` sample in
  [AgentEvalHQ/AgentEval](https://github.com/AgentEvalHQ/AgentEval) at commit
  `dc5369b16285a1365631138c7d287152c55083dd`.
- Historical evaluation inspiration only: the `Galaxus.RecommendationAgent.Evals` and
  `AgentEval.TravelDemo.Evals` samples at that same AgentEval commit.
- Canonical 0.35 benchmark references: the published NuGet consumer and `EvalJoin/02` sample.
- Imported and substantially revised: 2026-09-07 onward.
- AgentEval license: MIT; see `AgentEval-LICENSE.txt`.

The subject began as a same-author contribution to the AgentEval sample tree and has since been
substantially revised for VITRINE. Its bespoke historical eval machinery was not copied. VITRINE
references the published `AgentEval` `0.35.0-beta` package so it remains a standalone repository
rather than depending on a sibling checkout. The 0.35 adoption follows ADR-030's neutral meta lane
and ADR-032's benchmark/arm/runner/score plus standard output-store contracts. ADR-031's pack scope
was rejected; VITRINE does not copy the older TravelDemo snapshot/store pattern or create its own
pack format.
