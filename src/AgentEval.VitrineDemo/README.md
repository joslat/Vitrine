<!-- SPDX-License-Identifier: MIT -->
# Robin — VITRINE's synthetic e-commerce recommendation subject

> **Independent portfolio project.** This subject was created independently for VITRINE, a
> job-application and value-proposal sample prepared for Digitec Galaxus. It is not affiliated with,
> commissioned by, or endorsed by Digitec Galaxus. The catalogue, personas, histories, reviews,
> prices, and stock are synthetic. Measurements are real observations of this sample; no company
> system or customer data is used.

This project is the system under evaluation. It contains two implementations of the same product
discovery problem:

- **Demo01:** Robin, one `ChatClientAgent` using an observed read-only commerce tool surface.
- **Demo02:** an explicit five-executor MAF workflow with a conditional
  `CoverageReviewer → Discovery` loop-back.

The companion `AgentEval.VitrineDemo.Evals` project owns expectations and verdicts. This subject
emits facts and observations; it does not grade itself.

For the recruiter-facing intent, read the
[Digitec Galaxus value proposal](https://azuresamurai.blog/Vitrine/Vitrine-Digitec-Galaxus-Value-Proposal.html).
For the current end-to-end operating path, read the
[walkthrough](https://azuresamurai.blog/Vitrine/Vitrine-Walkthrough.html) and repository-root
[README](../../README.md).
For the optional provider-backed path, use the canonical
[Microsoft Foundry live-run setup](https://azuresamurai.blog/Vitrine/Vitrine-Live-Run-Setup.html);
the current client requires the Azure OpenAI resource endpoint and API key, not a Foundry project
endpoint or `az login` session.

## Run the subject

From the repository root:

```powershell
# Agent Demo01; the normal repository path remains offline
.\start.ps1 -Mode Demo01

# Workflow Demo02 with the real five-executor graph
.\start.ps1 -Mode Demo02 -NoRestore

# Optional: open the visual control room and choose an arm/persona before running
.\start.ps1 -Mode App

# Direct subject selector 1; the zero-model baseline remains the default
dotnet run --project src/AgentEval.VitrineDemo -- 1

# Real Demo01 agent and tools with the deterministic local chat client
dotnet run --project src/AgentEval.VitrineDemo -- 1 --scripted

# Model-backed subject execution requires both flags
dotnet run --project src/AgentEval.VitrineDemo -- 1 --live --confirm-paid
```

Credentials never change that default. `--offline`, `--scripted`, and `--live` are mutually
exclusive subject-arm selectors, and `--scripted` is valid only for Demo01. The standalone CLI rejects a provider-capable request
before execution unless it is explicitly confirmed: `--live`, `--real-vectors`, and
`--rebuild-embeddings` each require `--confirm-paid`. `--real-vectors` may embed queries live even
when the subject itself is offline.

The runnable subject exposes three deliberately named execution arms:

| Arm | Remote model | Purpose |
|---|---:|---|
| `ZeroModelBaseline` | No | Deterministic retrieval/workflow baseline. |
| `ScriptedAgent` | No | Real agent/workflow boundary with a deterministic local `IChatClient`; used by the normal control-room and test path. |
| `LiveAzure` | Yes | Explicit provider-backed subject execution; never selected merely because credentials exist. |

Paid evaluation is also not selected here implicitly. The named Eval01–Eval06 plans, readiness
checks, one-shot confirmation, model-call accounting, and sanitized receipts are owned by the eval
project.

## What is synthetic but realistic

- A validated 99-product catalogue spanning familiar retail categories.
- Fourteen authored personas with purchases, reviews, exclusions, and thin-signal cases.
- Catalogue search, similarity, complements, product detail, history, interest-map, department,
  price/stock, and presentation boundaries.
- A hybrid deterministic retrieval path combining an authored concept space with lexical evidence.
- A customer-facing recommendation artifact with cited catalogue evidence.

The four canonical paid-evaluation scenarios deliberately test different failure modes:

- Nadia: cross-category hiking, power, lighting, and photography intent.
- Sofia: consumable replenishment versus a missing durable capability.
- Marco: gift-derived false preference versus his own espresso evidence.
- Luca: safe abstention when one low-information purchase is insufficient.

Scenario titles, descriptions, and exact canonical queries are centralized in
[`Catalogue/PersonaScenarios.cs`](Catalogue/PersonaScenarios.cs). Evaluation-only ground truth,
criteria, and tool expectations remain in the eval project so they cannot leak into the subject.

## Architecture

The main source areas are:

- `Agents/` — Demo01 execution and its zero-model/scripted/live arm selection.
- `Demos/` — Demo01 and Demo02 orchestration, run options, and executable controls.
- `Domain/` — shared product, customer, signal, and recommendation contracts.
- `Evaluation/` — subject-side observation policy; expectations and verdicts stay in the eval project.
- `Workflows/` — Demo02 state, executors, routes, loop conditions, and typed workflow events.
- `Catalogue/` — synthetic products, personas, histories, reviews, and validation.
- `Retrieval/` — deterministic concept and lexical retrieval plus optional provider-backed paths.
- `Signals/` — interest derivation and explicit evidence references.
- `Tools/` — the observed, read-only agent function surface.
- `Guardrails/` — mechanical catalogue/evidence screening and final customer-answer screening.
- `Observability/` — correlated model/tool lifecycle events and usage states.
- `Rendering/` — bounded customer-facing composition, console output, and run reports.

The optional visual control room lives in the sibling `AgentEval.VitrineDemo.App` project; the
subject remains runnable directly without it.

The normative ownership and data flow are documented in
[`docs/Vitrine-Architecture.html`](https://azuresamurai.blog/Vitrine/Vitrine-Architecture.html).

## Safety and privacy boundaries

- Personalization disabled at setup is enforced at the tool boundary, not only in prompt text.
- Gift-derived and sensitive signals are screened before they become recommendations.
- Presented SKUs must resolve to the synthetic catalogue and retain allowed evidence.
- The customer-facing answer is screened separately from tool arguments.
- Model/tool events expose bounded allow-listed previews, correlation IDs, and explicit missing
  response states; credentials and endpoint values are never projected.
- All default subject and evaluation paths are offline.

These controls are demonstrations, not a security certification, privacy assessment, or claim
about Digitec Galaxus production controls.

## Evaluation boundary

The sibling eval project adds:

- the retained deterministic suite under `Evals/Offline`;
- use-case, comparison, and stochastic plans under `Evals/Live`;
- real AgentEval RedTeam jailbreak and hidden-instruction-extraction probes in Eval06;
- Wilson reliability, native paired comparisons, honest missing measurements, and durable local
  evidence.

See the [evaluation protocol](https://azuresamurai.blog/Vitrine/Vitrine-Evaluation-Protocol.html) for exact semantics and the
[offline report](https://azuresamurai.blog/Vitrine/reports/evals-offline.html) for one checked-in evidence artifact.

## Honest limits

- Synthetic scenarios are evaluation hypotheses, not observed customer behavior.
- No production conversion, revenue, margin, latency, return-rate, or satisfaction impact is
  measured here.
- A useful offline result does not establish live-model quality.
- A small paid stochastic run does not establish production reliability.
- A bounded RedTeam campaign is not penetration testing or compliance certification.
- Neither an agent nor a workflow is presumed superior; comparison evidence must decide for the
  selected cases and observation unit.

Historical predecessor design material is deliberately omitted from the public snapshot; its
commands, counts, and measurements are not the current VITRINE contract. Use the
[VITRINE retrieval deep dive](https://azuresamurai.blog/Vitrine/Vitrine-Retrieval-Deep-Dive.html) as the current reference.
