# SPDX-License-Identifier: MIT
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [switch] $NoRestore,

    # Bounded lane used by the CI-chain negative control; it executes this script and the
    # model-free gates without recursively launching the test suite or the 43 controls.
    [switch] $EvalProofOnly,

    [string] $ProofReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$planPath = Join-Path $PSScriptRoot "test-mocked.plan.json"
$plan = Get-Content -Raw -LiteralPath $planPath | ConvertFrom-Json
if ($plan.spdxLicense -cne "MIT" -or $plan.schemaVersion -ne 1 -or
    $plan.command -cne "dotnet-test" -or
    $plan.solution -cne "AgentEval.VitrineDemo.slnx" -or
    $plan.evalProject -cne "src/AgentEval.VitrineDemo.Evals/AgentEval.VitrineDemo.Evals.csproj" -or
    @($plan.evalArguments).Count -ne 1 -or @($plan.evalArguments)[0] -cne "--all" -or
    @($plan.evalProofArguments).Count -ne 1 -or @($plan.evalProofArguments)[0] -cne "--gates-only" -or
    $plan.testFilter -cne "Category!=LiveModel" -or
    $plan.failOnNonZero -ne $true) {
    throw "The committed mocked-test execution plan is invalid."
}
$solutionPath = Join-Path $repositoryRoot $plan.solution
$evalProjectPath = Join-Path $repositoryRoot $plan.evalProject

Push-Location $repositoryRoot
try {
    if (-not $NoRestore) {
        & dotnet restore $solutionPath
        if ($LASTEXITCODE -ne 0) { throw "Package restore failed with exit code $LASTEXITCODE." }
    }

    # Run the complete model-free eval executable itself; tests are evidence in addition to it.
    [string[]] $evalArguments = if ($EvalProofOnly) { @($plan.evalProofArguments) } else { @($plan.evalArguments) }
    if ($EvalProofOnly -and -not [string]::IsNullOrWhiteSpace($ProofReportPath)) {
        $evalArguments = @($evalArguments) + @("--json", [IO.Path]::GetFullPath($ProofReportPath))
    }
    $runOptions = @("run", "--project", $evalProjectPath, "--configuration", $Configuration, "--no-restore")
    if ($EvalProofOnly) { $runOptions += "--no-build" }
    & dotnet @runOptions -- @evalArguments
    if ($plan.failOnNonZero -and $LASTEXITCODE -ne 0) {
        throw "Offline evaluation chain failed with exit code $LASTEXITCODE."
    }

    if ($EvalProofOnly) { return }

    # The filter is the safety boundary even if the caller's shell already opted into live tests.
    & dotnet test $solutionPath --configuration $Configuration --no-restore --filter $plan.testFilter
    if ($plan.failOnNonZero -and $LASTEXITCODE -ne 0) {
        throw "Mocked/offline tests failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
