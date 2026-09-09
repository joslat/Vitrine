<!-- SPDX-License-Identifier: MIT -->
# VITRINE

> **Independent job-application sample for Digitec Galaxus.** I built VITRINE to show how I can
> connect realistic e-commerce product discovery with an evaluation discipline: explicit use
> cases, independent criteria, deterministic regression evidence, opt-in LLM and stochastic
> measurements, red-team safety probes, and durable proof. It is not affiliated with,
> commissioned by, or endorsed by Digitec Galaxus. All catalogue, persona, and history data is
> synthetic; its results do not measure a Digitec Galaxus system.

Start with the **[VITRINE documentation hub](docs/index.html)**. It is the canonical, shareable
front door and links to the [value proposal](docs/Vitrine-Digitec-Galaxus-Value-Proposal.html),
[operator walkthrough](docs/Vitrine-Walkthrough.html),
[architecture](docs/Vitrine-Architecture.html),
[evaluation protocol](docs/Vitrine-Evaluation-Protocol.html),
[retrieval deep dive](docs/Vitrine-Retrieval-Deep-Dive.html), and
[verification receipt](docs/Vitrine-Verification.html).

VITRINE is also an applied demonstration of my work on
[AgentEval](https://github.com/AgentEvalHQ/AgentEval): the framework's admitted checks, benchmark
arms, native comparisons, statistics, output stores, and real `RedTeamRunner` are exercised
against a working synthetic commerce subject rather than described in isolation.

## What runs

- **Demo01:** one `ChatClientAgent` named Robin using 13 observed read-only functions.
- **Demo02:** a five-executor MAF discovery workflow with one visible reviewer → discovery loop.
- **Offline evals:** six admitted production gates, a persisted five-check recommendation
  benchmark, and 43 registered healthy → defect → recovery diagnostics. This is the default.
- **Paid evals:** Eval01–Eval05 measure four shared shopping scenarios across agent/workflow arms,
  including stochastic Wilson intervals and a native paired comparison; Eval06 runs real
  AgentEval jailbreak and canary-backed system-prompt-extraction probes against fresh Robin agents.
- **Evidence control room:** runtime-derived graphs, correlated timeline evidence, an evaluation
  board, integrity-protected JSON/HTML export, and replay that executes nothing.

The repository keeps the cost boundary visible in source:

- `src/AgentEval.VitrineDemo.Evals/Evals/Offline` — credential-free suite and diagnostics.
- `src/AgentEval.VitrineDemo.Evals/Evals/Live` — named, explicitly confirmed paid plans.

## Ten-minute path

Prerequisite: .NET SDK 10.

```powershell
# Open the control room; its default subject arm is deterministic and local.
.\start.ps1 -Mode App

# Run the single agent and workflow demos without an external provider.
.\start.ps1 -Mode Demo01
.\start.ps1 -Mode Demo02 -NoRestore

# Run the complete offline evidence chain and its diagnostics.
.\start.ps1 -Mode Evals -NoRestore
.\start.ps1 -Mode Controls -NoRestore

# Plant one isolated catalogue defect; detection intentionally exits non-zero.
.\start.ps1 -Mode Ablation -NoRestore
```

Direct commands:

```powershell
dotnet build AgentEval.VitrineDemo.slnx
dotnet test AgentEval.VitrineDemo.slnx --filter "Category!=LiveModel"
dotnet run --project src/AgentEval.VitrineDemo.Evals -- --all
dotnet run --project src/AgentEval.VitrineDemo.Evals -- --self-test

# Standalone subject selectors 1-6 are deterministic and offline by default.
dotnet run --project src/AgentEval.VitrineDemo -- 1
```

## Execution and cost boundary

The subject demos expose `ZeroModelBaseline`, deterministic local `ScriptedAgent`, and explicitly
selected `LiveAzure` arms. The evaluation runner separately exposes `OfflineSuite` plus six named
paid plans; it has no implicit live profile. Credentials alone never change the selected lane.

The standalone subject CLI also fails closed: selectors 1–6 remain offline unless both `--live`
and `--confirm-paid` are present. `--real-vectors` and `--rebuild-embeddings` can perform live
embedding work, so each also requires `--confirm-paid`. Every paid eval plan requires the same
confirmation, and the app requires the equivalent confirmation after showing the planned workload.
Readiness is checked before model execution; failure persists a non-success receipt and never
substitutes an offline result.

```powershell
# One deliberately explicit paid example.
dotnet run --project src/AgentEval.VitrineDemo.Evals -- `
  --eval-plan eval01-agent --scenario nadia-cross-category --confirm-paid

# One deliberately explicit provider-backed subject example.
dotnet run --project src/AgentEval.VitrineDemo -- 1 --live --confirm-paid
```

Optional live code reads `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`, and
`AZURE_OPENAI_DEPLOYMENT`. VITRINE never prints, persists, fingerprints, or hashes the key or
endpoint URL. Raw Eval06 prompts, responses, extraction canaries, system instructions, provider
messages, and exception text are excluded from its receipt.

Eval process classes are: `0` pass, `1` measured failure, `2` invalid arguments, `3` not measured,
and `4` infrastructure failure. Missing evidence remains absent—it is never displayed as zero.

## Repository map

- `src/AgentEval.VitrineDemo` — synthetic catalogue, personas, retrieval, tools, guardrails,
  Demo01, and Demo02.
- `src/AgentEval.VitrineDemo.Evals` — AgentEval integration, offline/live plans, CLI, reports, and
  local evidence persistence.
- `src/AgentEval.VitrineDemo.App` — Avalonia evidence control room.
- `tests/AgentEval.VitrineDemo.Tests` — credential-free integration, artifact, UI, graph,
  redaction, and failure-state coverage; live contracts are opt-in only.
- `docs` — the canonical HTML documentation site and sanitized generated evidence.
- `MIGRATION.md` — repository-native AgentEval 0.35 admission/API migration ledger.
- `eng/upstream` — source provenance and upstream MIT license.

Public-use boundaries are documented in [NOTICE.md](NOTICE.md), [DATA-PROVENANCE.md](DATA-PROVENANCE.md),
[SECURITY.md](SECURITY.md), and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The source is
available under the [MIT License](LICENSE).

## Verification

The current credential-free receipt is:

- Release build: 0 warnings, 0 errors.
- `Category!=LiveModel`: 336/336 passed, 0 failed, 0 skipped.
- Offline admitted-check self-test: exit 0.
- Product gates: 6/6 pass; registered diagnostics: 43/43 caught.
- Catalogue integrity self-test: expected exit 1, followed by a restored 6/6 exit-0 run.
- No live/provider call was made by the final acceptance commands.

See the [dated verification page](docs/Vitrine-Verification.html) for the exact receipt and
incidents, and [MIGRATION.md](MIGRATION.md) for every admitted check, native floor, ablation, and
deliberately retained diagnostic/collector boundary.
