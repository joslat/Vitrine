<!-- SPDX-License-Identifier: MIT -->
# Contributing

Issues and focused pull requests are welcome. Before proposing a change:

1. read `NOTICE.md`, `DATA-PROVENANCE.md`, and the HTML evaluation protocol;
2. keep the default path credential-free and deterministic;
3. do not add company, customer, secret, endpoint, or private infrastructure data;
4. do not run a live plan as part of tests or CI;
5. preserve AgentEval's native measurement, missingness, benchmark, and output-store semantics;
6. add tests for changed behavior and update the canonical HTML documentation when contracts move.

Run locally:

```powershell
dotnet restore AgentEval.VitrineDemo.slnx --locked-mode
dotnet build AgentEval.VitrineDemo.slnx -c Release --no-restore -warnaserror
dotnet test tests\AgentEval.VitrineDemo.Tests\AgentEval.VitrineDemo.Tests.csproj `
  -c Release --no-build --no-restore --filter "Category!=LiveModel"
dotnet run --project src\AgentEval.VitrineDemo.Evals -c Release --no-build -- --self-test
dotnet run --project src\AgentEval.VitrineDemo.Evals -c Release --no-build -- --all
pwsh -File eng\verify-publication.ps1
```

By contributing, you agree that your contribution is licensed under this repository's MIT license.
