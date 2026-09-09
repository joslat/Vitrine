// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.App.Artifacts;
using AgentEval.VitrineDemo.App.Models;
using AgentEval.VitrineDemo.App.Runtime;
using AgentEval.VitrineDemo.App.ViewModels;
using AgentEval.VitrineDemo.Evals;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Rendering;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentEval.VitrineDemo.Tests;

public sealed class ArtifactAndReplayTests
{
    [Fact]
    public async Task ExportContainsNoEnvironmentSecretOrEndpointAndRoundTrips()
    {
        const string endpoint = "https://sentinel-vitrine-export.example.invalid/path";
        const string key = "SENTINEL-VITRINE-EXPORT-SECRET";
        var originalEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var originalKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", endpoint);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", key);
            await using var coordinator = new VitrineRunCoordinator();
            var outcome = await coordinator.RunAsync(new(VitrineRunMode.Evals, Personas.NadiaUserId));
            var artifact = VitrineArtifactSerializer.Create(outcome);

            var json = VitrineArtifactSerializer.Serialize(artifact);
            var html = VitrineHtmlReport.Render(artifact);

            Assert.DoesNotContain(endpoint, json, StringComparison.Ordinal);
            Assert.DoesNotContain(key, json, StringComparison.Ordinal);
            Assert.DoesNotContain(endpoint, html, StringComparison.Ordinal);
            Assert.DoesNotContain(key, html, StringComparison.Ordinal);
            var roundTripped = VitrineArtifactSerializer.Deserialize(json);
            Assert.True(VitrineArtifactSerializer.Verify(roundTripped));
            Assert.Equal(json, VitrineArtifactSerializer.Serialize(roundTripped));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", originalEndpoint);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", originalKey);
        }
    }

    [Fact]
    public void MissingMeasurementStaysNullInJsonAndNotMeasuredInHtml()
    {
        var suite = new SuiteResult(
            [GateResult.NotMeasured("Absent measurement", null, "NOT MEASURED: fixture")],
            []);
        var outcome = new VitrineRunOutcome(
            Guid.NewGuid(),
            new(VitrineRunMode.Evals, Personas.NadiaUserId),
            VitrineGraphFactory.ForEvaluationSuite(),
            [],
            Evaluation: suite);
        var artifact = VitrineArtifactSerializer.Create(outcome);

        var json = VitrineArtifactSerializer.Serialize(artifact);
        var html = VitrineHtmlReport.Render(artifact);

        Assert.Contains("\"score\": null", json, StringComparison.Ordinal);
        Assert.Contains("NOT MEASURED", html, StringComparison.Ordinal);
        Assert.DoesNotContain("score 0", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("not-measured", artifact.Result.Status);
        Assert.Equal(3, artifact.Result.ProcessEquivalentExitCode);
    }

    [Fact]
    public async Task TamperingIsRejectedAndReplayOnlyMovesAnImmutableCursor()
    {
        await using var coordinator = new VitrineRunCoordinator();
        var outcome = await coordinator.RunAsync(new(VitrineRunMode.Demo01, Personas.NadiaUserId));
        var artifact = VitrineArtifactSerializer.Create(outcome);
        var json = VitrineArtifactSerializer.Serialize(artifact);
        var tampered = json.Replace(Personas.NadiaUserId, "USR-TAMPERED", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Deserialize(tampered));

        var replay = new VitrineReplaySession(artifact);
        var first = replay.MoveNext();
        var second = replay.MoveNext();
        replay.MovePrevious();

        Assert.Same(first, replay.Current);
        Assert.NotSame(first, second);
        Assert.Equal(outcome.Events, artifact.Events);
        Assert.Null(outcome.ProcessEquivalentExitCode);
        Assert.Null(artifact.Result.ProcessEquivalentExitCode);
        Assert.Contains("Exit</div><div class=\"metric\">NOT MEASURED", VitrineHtmlReport.Render(artifact), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplayDefensivelyFreezesCallerOwnedCollections()
    {
        await using var coordinator = new VitrineRunCoordinator();
        var artifact = VitrineArtifactSerializer.Create(await coordinator.RunAsync(
            new(VitrineRunMode.Demo01, Personas.NadiaUserId)));
        var callerEvents = artifact.Events.ToList();
        var callerNodes = artifact.Graph.Nodes.ToList();
        var callerOwned = artifact with
        {
            Events = callerEvents,
            Graph = artifact.Graph with { Nodes = callerNodes },
        };

        var replay = new VitrineReplaySession(callerOwned);
        callerEvents.Clear();
        callerNodes.Clear();

        Assert.NotEmpty(replay.Artifact.Events);
        Assert.NotEmpty(replay.Artifact.Graph.Nodes);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<VitrineEvent>)replay.Artifact.Events).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<VitrineGraphNode>)replay.Artifact.Graph.Nodes).Clear());
    }

    [Fact]
    public async Task RichArtifactsPreserveTheExactScreenedAgentAndWorkflowOutcomes()
    {
        await using var coordinator = new VitrineRunCoordinator();
        var demo01Outcome = await coordinator.RunAsync(new(VitrineRunMode.Demo01, Personas.NadiaUserId));
        var demo01Created = VitrineArtifactSerializer.Create(demo01Outcome);
        var demo01 = VitrineArtifactSerializer.Deserialize(VitrineArtifactSerializer.Serialize(demo01Created));
        var agent = Assert.IsType<VitrineDemo01Snapshot>(demo01.Result.Demo01);
        var expectedAgentSkus = demo01Outcome.Recommendation!.Outcome!.Cleaned.AllPresented
            .Select(item => item.ProductId).ToArray();
        var expectedAgentAnswer = RecommendationArtifactComposer.Compose(demo01Outcome.Recommendation)
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

        Assert.Equal(VitrineRunArtifact.CurrentSchemaVersion, demo01.SchemaVersion);
        Assert.Equal(expectedAgentAnswer, agent.CustomerFacingAnswer);
        Assert.NotEmpty(agent.Recommendations);
        Assert.Equal(expectedAgentSkus, agent.Recommendations.Select(item => item.Sku));
        Assert.NotNull(agent.Ledger);
        Assert.Equal(agent.Ledger!.OutputCount, agent.Recommendations.Count);
        Assert.Equal(demo01Outcome.Recommendation.RegisteredToolNames, agent.RegisteredTools);
        Assert.Throws<NotSupportedException>(() => ((IList<VitrineRecommendationSnapshot>)agent.Recommendations).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)agent.RegisteredTools).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<VitrineLedgerEntrySnapshot>)agent.Ledger.Entries).Clear());
        var agentHtml = VitrineHtmlReport.Render(demo01);
        var (_, agentInspector) = VitrineOutcomeInspector.Describe(demo01);
        Assert.Contains("Screened Demo01 outcome", agentHtml, StringComparison.Ordinal);
        foreach (var sku in expectedAgentSkus)
        {
            Assert.Contains(sku, agentHtml, StringComparison.Ordinal);
            Assert.Contains(sku, agentInspector, StringComparison.Ordinal);
        }
        foreach (var tool in agent.RegisteredTools)
        {
            Assert.Contains(tool, agentHtml, StringComparison.Ordinal);
            Assert.Contains(tool, agentInspector, StringComparison.Ordinal);
        }
        Assert.Contains("Show sanitized tool parameters", agentHtml, StringComparison.Ordinal);
        Assert.Contains("Show sanitized tool response", agentHtml, StringComparison.Ordinal);
        Assert.Contains("operation ", agentHtml, StringComparison.Ordinal);
        Assert.Contains("Execution count", agentHtml, StringComparison.Ordinal);
        Assert.Contains("Traversal count", agentHtml, StringComparison.Ordinal);
        Assert.Contains("ToolExecutionStarted · DONE", agentHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(" · STARTED · ", agentHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(" · Active · ", agentHtml, StringComparison.Ordinal);

        var demo02Outcome = await coordinator.RunAsync(new(VitrineRunMode.Demo02, Personas.NadiaUserId));
        var demo02Created = VitrineArtifactSerializer.Create(demo02Outcome);
        var demo02 = VitrineArtifactSerializer.Deserialize(VitrineArtifactSerializer.Serialize(demo02Created));
        var workflow = Assert.IsType<VitrineDemo02Snapshot>(demo02.Result.Demo02);
        var expectedWorkflowSkus = demo02Outcome.Workflow!.State.Presented.Select(item => item.ProductId).ToArray();

        Assert.Equal(demo02Outcome.Workflow.State.FinalAnswer.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim(),
            workflow.CustomerFacingAnswer);
        Assert.Equal(demo02Outcome.Workflow.RoutesTaken, workflow.RoutesTaken);
        Assert.Equal(expectedWorkflowSkus, workflow.Recommendations.Select(item => item.Sku));
        Assert.Contains(Galaxus.RecommendationAgent.Workflows.DiscoveryRouteIds.ReviewToMoreDiscovery,
            workflow.RoutesTaken);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)workflow.RoutesTaken).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)workflow.ExecutorIds).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<VitrineWorkflowInterestSnapshot>)workflow.Interests).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<VitrineRecommendationSnapshot>)workflow.Recommendations).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)workflow.OpenGaps).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)workflow.DroppedSkus).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)workflow.DegradedNotes).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)workflow.ExecutorFailures).Clear());
        var workflowHtml = VitrineHtmlReport.Render(demo02);
        var (_, workflowInspector) = VitrineOutcomeInspector.Describe(demo02);
        Assert.Contains("Screened Demo02 workflow outcome", workflowHtml, StringComparison.Ordinal);
        Assert.Contains("Routes actually taken", workflowHtml, StringComparison.Ordinal);
        Assert.Contains("Show sanitized model input", workflowHtml, StringComparison.Ordinal);
        Assert.Contains("Show sanitized model output", workflowHtml, StringComparison.Ordinal);
        Assert.Contains("USER INPUT", workflowHtml, StringComparison.Ordinal);
        Assert.True(demo02.Graph.Edges.Any(static edge => edge.IsLoopBack),
            string.Join(" | ", demo02.Graph.Edges.Select(static edge => $"{edge.Id}:{edge.IsLoopBack}")));
        Assert.Contains("BACK &#183;", workflowHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(" C ", workflowHtml, StringComparison.Ordinal);
        Assert.Contains("This event records a typed state transition; no request, response, or tool payload applies.", workflowHtml, StringComparison.Ordinal);
        foreach (var route in workflow.RoutesTaken)
        {
            Assert.Contains(route, workflowHtml, StringComparison.Ordinal);
            Assert.Contains(route, workflowInspector, StringComparison.Ordinal);
        }
        foreach (var sku in expectedWorkflowSkus)
        {
            Assert.Contains(sku, workflowHtml, StringComparison.Ordinal);
            Assert.Contains(sku, workflowInspector, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EvaluationArtifactCarriesFullAgentEvalProvenanceAcrossJsonHtmlAndInspector()
    {
        var sourceGate = await EvaluationSuite.JudgedGateAsync(default);
        var source = Assert.IsType<AgentEvalProvenance>(sourceGate.AgentEval);
        var outcome = new VitrineRunOutcome(
            Guid.NewGuid(),
            new(VitrineRunMode.Evals, Personas.NadiaUserId),
            VitrineGraphFactory.ForEvaluationSuite(),
            [],
            Evaluation: new SuiteResult([sourceGate], []));

        var created = VitrineArtifactSerializer.Create(outcome);
        var json = VitrineArtifactSerializer.Serialize(created);
        var artifact = VitrineArtifactSerializer.Deserialize(json);
        var snapshot = Assert.IsType<VitrineAgentEvalProvenanceSnapshot>(Assert.Single(artifact.Result.Gates).AgentEval);
        var html = VitrineHtmlReport.Render(artifact);
        var (_, inspector) = VitrineOutcomeInspector.Describe(artifact);

        Assert.Equal(source.LibraryType, snapshot.LibraryType);
        Assert.Equal(source.Mechanism, snapshot.Mechanism);
        Assert.Equal(source.Subject, snapshot.Subject);
        Assert.Equal(source.SnapshotPolicy, snapshot.SnapshotPolicy);
        Assert.Equal(source.ObservationProducer, snapshot.ObservationProducer);
        Assert.Equal(source.AcceptanceEvaluator, snapshot.AcceptanceEvaluator);
        Assert.Equal(source.SubjectSuppliedPassFail, snapshot.SubjectSuppliedPassFail);
        Assert.Equal(source.Observations.Select(item => item.Id), snapshot.Observations.Select(item => item.Id));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<VitrineAgentEvalObservationSnapshot>)snapshot.Observations).Clear());
        Assert.Contains("\"agentEval\"", json, StringComparison.Ordinal);
        Assert.Contains("\"observationProducer\"", json, StringComparison.Ordinal);
        Assert.Contains("\"acceptanceEvaluator\"", json, StringComparison.Ordinal);
        Assert.Contains("\"subjectSuppliedPassFail\": false", json, StringComparison.Ordinal);
        Assert.Contains(snapshot.LibraryType, html, StringComparison.Ordinal);
        Assert.Contains(snapshot.LibraryType, inspector, StringComparison.Ordinal);
        Assert.Contains(snapshot.ObservationProducer, html, StringComparison.Ordinal);
        Assert.Contains(snapshot.ObservationProducer, inspector, StringComparison.Ordinal);
        Assert.Contains(snapshot.AcceptanceEvaluator, html, StringComparison.Ordinal);
        Assert.Contains(snapshot.AcceptanceEvaluator, inspector, StringComparison.Ordinal);
        Assert.Contains("Subject supplied pass/fail: no", html, StringComparison.Ordinal);
        Assert.Contains("Subject supplied pass/fail: no", inspector, StringComparison.Ordinal);
        foreach (var observation in snapshot.Observations)
        {
            Assert.Contains(observation.Id, html, StringComparison.Ordinal);
            Assert.Contains(observation.Id, inspector, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ArtifactSanitizesSecretsBeforeIntegrityInputAndHtmlEncodesUntrustedContent()
    {
        const string endpoint = "https://sentinel-vitrine-hash.example.invalid/private";
        const string key = "SENTINEL-VITRINE-HASH-SECRET";
        const string malicious = "<script data-vitrine='attack'>alert('unsafe')</script>";
        var graph = new VitrineGraphSnapshot(
            $"{malicious} endpoint={endpoint} api_key={key}",
            VitrineGraphSource.EvaluationService,
            [new("node", malicious, "test", $"endpoint={endpoint}; api_key={key}")],
            []);
        var outcome = new VitrineRunOutcome(
            Guid.NewGuid(), new(VitrineRunMode.Evals, Personas.NadiaUserId), graph, []);

        var artifact = VitrineArtifactSerializer.Create(outcome);
        var integrityInput = VitrineArtifactSerializer.SerializeIntegrityPayload(artifact);
        var json = VitrineArtifactSerializer.Serialize(artifact);
        var html = VitrineHtmlReport.Render(artifact);

        Assert.True(VitrineArtifactSerializer.Verify(artifact));
        Assert.DoesNotContain(endpoint, integrityInput, StringComparison.Ordinal);
        Assert.DoesNotContain(key, integrityInput, StringComparison.Ordinal);
        Assert.DoesNotContain(endpoint, json, StringComparison.Ordinal);
        Assert.DoesNotContain(key, json, StringComparison.Ordinal);
        Assert.DoesNotContain(endpoint, html, StringComparison.Ordinal);
        Assert.DoesNotContain(key, html, StringComparison.Ordinal);
        Assert.DoesNotContain(malicious, html, StringComparison.Ordinal);
        Assert.Contains("&lt;script", html, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", integrityInput, StringComparison.Ordinal);
        Assert.Contains("api_key=[REDACTED]", integrityInput, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectArtifactsRejectSecretPatternsAcrossNestedBranchesBeforeIntegritySerialization()
    {
        const string endpoint = "https://sentinel-vitrine-direct.example.invalid/private";
        const string assignment = "api_key=SENTINEL-VITRINE-DIRECT-SECRET";
        var artifact = CreateNestedEvaluationArtifact();
        var node = Assert.Single(artifact.Graph.Nodes);
        var runtimeEvent = Assert.Single(artifact.Events);
        var gate = Assert.Single(artifact.Result.Gates);
        var provenance = Assert.IsType<VitrineAgentEvalProvenanceSnapshot>(gate.AgentEval);
        var observation = Assert.Single(provenance.Observations);
        var control = Assert.Single(artifact.Result.Controls);
        var safeJson = VitrineArtifactSerializer.Serialize(artifact);
        var safeHtml = VitrineHtmlReport.Render(artifact);
        var (_, safeInspector) = VitrineOutcomeInspector.Describe(artifact);

        Assert.Equal("synthetic tranche", control.Tranche);
        Assert.Equal(ControlScopeClass.BoundaryCalibrationFixture, control.ScopeClass);
        Assert.Contains("\"tranche\": \"synthetic tranche\"", safeJson, StringComparison.Ordinal);
        Assert.Contains("\"scopeClass\": \"boundaryCalibrationFixture\"", safeJson, StringComparison.Ordinal);
        Assert.Contains("synthetic tranche", safeHtml, StringComparison.Ordinal);
        Assert.Contains(nameof(ControlScopeClass.BoundaryCalibrationFixture), safeHtml, StringComparison.Ordinal);
        Assert.Contains("synthetic tranche", safeInspector, StringComparison.Ordinal);
        Assert.Contains(nameof(ControlScopeClass.BoundaryCalibrationFixture), safeInspector, StringComparison.Ordinal);
        VitrineRunArtifact[] unsafeArtifacts =
        [
            artifact with
            {
                Graph = artifact.Graph with { Nodes = [node with { Description = endpoint }] },
            },
            artifact with
            {
                Events = [runtimeEvent with { SanitizedPayload = assignment }],
            },
            artifact with
            {
                Result = artifact.Result with
                {
                    Gates = [gate with
                    {
                        AgentEval = provenance with
                        {
                            Observations = [observation with { Surface = endpoint }],
                        },
                    }],
                },
            },
            artifact with
            {
                Result = artifact.Result with
                {
                    Controls = [control with { Evaluator = assignment }],
                },
            },
        ];

        foreach (var unsafeArtifact in unsafeArtifacts)
        {
            Assert.False(VitrineArtifactSerializer.Verify(unsafeArtifact));

            var payloadError = Assert.Throws<InvalidDataException>(() =>
                VitrineArtifactSerializer.SerializeIntegrityPayload(unsafeArtifact));
            Assert.Equal("Artifact contains disallowed secret-bearing content.", payloadError.Message);
            Assert.DoesNotContain(endpoint, payloadError.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(assignment, payloadError.Message, StringComparison.Ordinal);

            var serializeError = Assert.Throws<InvalidDataException>(() =>
                VitrineArtifactSerializer.Serialize(unsafeArtifact));
            Assert.Equal("Artifact contains disallowed secret-bearing content.", serializeError.Message);

            var htmlError = Assert.Throws<InvalidDataException>(() => VitrineHtmlReport.Render(unsafeArtifact));
            Assert.DoesNotContain(endpoint, htmlError.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(assignment, htmlError.Message, StringComparison.Ordinal);

            var inspectorError = Assert.Throws<InvalidDataException>(() =>
                VitrineOutcomeInspector.Describe(unsafeArtifact));
            Assert.DoesNotContain(endpoint, inspectorError.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(assignment, inspectorError.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ForgedJsonWithNestedSecretPatternIsRejectedBeforeIntegrityVerification()
    {
        const string safeSurface = "safe-observation-surface";
        const string unsafeSurface = "endpoint=https://sentinel-vitrine-forged.example.invalid/private";
        var json = VitrineArtifactSerializer.Serialize(CreateNestedEvaluationArtifact());
        var forged = json.Replace(safeSurface, unsafeSurface, StringComparison.Ordinal);

        Assert.NotEqual(json, forged);
        var error = Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Deserialize(forged));

        Assert.Equal("Artifact contains disallowed secret-bearing content.", error.Message);
        Assert.DoesNotContain(unsafeSurface, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredSecretsAreRejectedBareOrEmbeddedAcrossCreateIntegrityJsonAndRenderBoundaries()
    {
        const string apiKey = "SENTINEL-VITRINE-OPAQUE-KEY-4C8E63A1";
        const string endpoint = "SENTINEL-VITRINE-OPAQUE-ENDPOINT";
        const string safeSurface = "safe-observation-surface";
        var safeArtifact = CreateNestedEvaluationArtifact();
        var safeJson = VitrineArtifactSerializer.Serialize(safeArtifact);
        var originalEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var originalKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", endpoint);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", apiKey);

            foreach (var configuredValue in new[] { apiKey, endpoint.ToLowerInvariant() })
            {
                foreach (var forbiddenValue in new[] { configuredValue, $"safe-prefix::{configuredValue}::safe-suffix" })
                {
                    var runId = Guid.NewGuid();
                    var unsafeOutcome = new VitrineRunOutcome(
                        runId,
                        new(VitrineRunMode.Evals, Personas.NadiaUserId),
                        new VitrineGraphSnapshot(
                            "Synthetic graph",
                            VitrineGraphSource.EvaluationService,
                            [new("subject", "Subject", "agent", forbiddenValue)],
                            []),
                        []);
                    var createError = Assert.Throws<InvalidDataException>(() =>
                        VitrineArtifactSerializer.Create(unsafeOutcome));
                    Assert.Equal("Artifact contains disallowed secret-bearing content.", createError.Message);
                    Assert.DoesNotContain(configuredValue, createError.Message, StringComparison.OrdinalIgnoreCase);

                    var gate = Assert.Single(safeArtifact.Result.Gates);
                    var provenance = Assert.IsType<VitrineAgentEvalProvenanceSnapshot>(gate.AgentEval);
                    var observation = Assert.Single(provenance.Observations);
                    var unsafeArtifact = safeArtifact with
                    {
                        Result = safeArtifact.Result with
                        {
                            Gates = [gate with
                            {
                                AgentEval = provenance with
                                {
                                    Observations = [observation with { Surface = forbiddenValue }],
                                },
                            }],
                        },
                    };

                    Assert.False(VitrineArtifactSerializer.Verify(unsafeArtifact));
                    AssertValueBlindRejection(
                        () => VitrineArtifactSerializer.SerializeIntegrityPayload(unsafeArtifact),
                        configuredValue);
                    AssertValueBlindRejection(
                        () => VitrineArtifactSerializer.Serialize(unsafeArtifact),
                        configuredValue);
                    AssertValueBlindRejection(
                        () => VitrineHtmlReport.Render(unsafeArtifact),
                        configuredValue,
                        expectedMessage: "Artifact integrity verification failed.");
                    AssertValueBlindRejection(
                        () => VitrineOutcomeInspector.Describe(unsafeArtifact),
                        configuredValue,
                        expectedMessage: "Artifact integrity verification failed.");

                    var forgedJson = safeJson.Replace(safeSurface, forbiddenValue, StringComparison.Ordinal);
                    Assert.NotEqual(safeJson, forgedJson);
                    AssertValueBlindRejection(
                        () => VitrineArtifactSerializer.Deserialize(forgedJson),
                        configuredValue);
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", originalEndpoint);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", originalKey);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void VerifyRejectsNonCanonicalIntegrityWithoutHashing(string integrity)
    {
        var artifact = CreateNestedEvaluationArtifact() with { IntegritySha256 = integrity };

        Assert.False(VitrineArtifactSerializer.Verify(artifact));
    }

    [Fact]
    public void ArtifactPreservesNullWorkflowFieldsAndLabelsLiveExecutionHonestly()
    {
        var outcome = new VitrineRunOutcome(
            Guid.NewGuid(),
            new(VitrineRunMode.Demo01, Personas.NadiaUserId,
                Demo01Arm: Galaxus.RecommendationAgent.Demos.RecommendationExecutionArm.LiveAzure),
            VitrineGraphFactory.FromRegisteredDemo01Functions(),
            []);

        var artifact = VitrineArtifactSerializer.Create(outcome);
        var json = VitrineArtifactSerializer.Serialize(artifact);
        var html = VitrineHtmlReport.Render(artifact);

        Assert.Null(artifact.Result.WorkflowStopReason);
        Assert.Contains("\"workflowStopReason\": null", json, StringComparison.Ordinal);
        Assert.Contains("SANITIZED · LIVE AZURE ARTIFACT", html, StringComparison.Ordinal);
        Assert.DoesNotContain("SANITIZED · OFFLINE ARTIFACT", html, StringComparison.Ordinal);
        Assert.Contains("Model calls</div><div class=\"metric\">NOT MEASURED", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingVerifiedRecommendationMeasurementsStayNullAndRenderNotMeasured()
    {
        await using var coordinator = new VitrineRunCoordinator();
        var outcome = await coordinator.RunAsync(new(VitrineRunMode.Demo01, Personas.NadiaUserId));
        var recommendation = Assert.IsType<Galaxus.RecommendationAgent.Demos.RecommendationRunResult>(outcome.Recommendation);
        var screened = Assert.IsType<Galaxus.RecommendationAgent.Guardrails.GuardrailOutcome>(recommendation.Outcome);
        var withoutMeasurements = recommendation with
        {
            Outcome = screened with
            {
                VerifiedPrices = new Dictionary<string, Galaxus.RecommendationAgent.Guardrails.PriceStockSnapshot>(),
            },
        };
        var artifact = VitrineArtifactSerializer.Create(outcome with { Recommendation = withoutMeasurements });
        var agent = Assert.IsType<VitrineDemo01Snapshot>(artifact.Result.Demo01);
        var first = Assert.Single(agent.Recommendations.Take(1));
        var json = VitrineArtifactSerializer.Serialize(artifact);
        var html = VitrineHtmlReport.Render(artifact);
        var (_, inspector) = VitrineOutcomeInspector.Describe(artifact);

        Assert.Null(first.VerifiedPriceChf);
        Assert.Null(first.StockUnits);
        Assert.Null(first.DeliveryEstimateDays);
        Assert.Contains("\"verifiedPriceChf\": null", json, StringComparison.Ordinal);
        Assert.Contains("<span class=\"not\">NOT MEASURED</span>", html, StringComparison.Ordinal);
        Assert.Contains("price NOT MEASURED · stock NOT MEASURED · delivery NOT MEASURED", inspector,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlGraphCoordinatesAreInvariantUnderCommaDecimalCultures()
    {
        var nodes = Enumerable.Range(1, 7)
            .Select(index => new VitrineGraphNode($"node-{index}", $"Node {index}", "executor"))
            .ToArray();
        var graph = new VitrineGraphSnapshot("fractional layout", VitrineGraphSource.MafWorkflow, nodes, []);
        var artifact = VitrineArtifactSerializer.Create(new VitrineRunOutcome(
            Guid.NewGuid(), new(VitrineRunMode.Demo02, Personas.NadiaUserId), graph, []));
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var html = VitrineHtmlReport.Render(artifact);

            Assert.Contains("243.333", html, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex("(?:x1|x2|y1|y2|x|y)=\\\"[0-9]+,[0-9]+", RegexOptions.CultureInvariant), html);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void StoreRedactsBeforeStorage()
    {
        var store = new VitrineEventStore();
        store.Append(new(VitrineEventCategory.System, "test", VitrineEventDisposition.Neutral,
            "source", "target", "payload", "endpoint=https://unsafe.example.invalid api_key=hidden"));

        var stored = Assert.Single(store.Snapshot());
        Assert.DoesNotContain("unsafe.example.invalid", stored.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", stored.Detail, StringComparison.Ordinal);
    }

    private static VitrineRunArtifact CreateNestedEvaluationArtifact()
    {
        var runId = Guid.NewGuid();
        var provenance = new AgentEvalProvenance(
            "synthetic",
            "AgentEval.Tests.Synthetic",
            "synthetic mechanism",
            "synthetic subject",
            [new("synthetic-observation", "measured", 1.0, "safe-observation-surface", 1)],
            "test-owned immutable snapshot",
            "independent producer",
            "independent evaluator",
            subjectSuppliedPassFail: false);
        var gate = new GateResult("Synthetic gate", true, 1.0,
            AgentEval.Evals.Meta.ChanceFloor.UniformChoice(2), "safe gate evidence")
        {
            AgentEval = provenance,
        };
        var control = new ControlResult(
            "NC-SYNTHETIC", "Synthetic control", "contract", true, true, "safe control evidence")
        {
            Target = "synthetic target",
            ObservationProducer = "independent producer",
            Evaluator = "independent evaluator",
            Tranche = "synthetic tranche",
        };
        var graph = new VitrineGraphSnapshot(
            "Synthetic evaluation graph",
            VitrineGraphSource.EvaluationService,
            [new("subject", "Subject", "agent", "safe node description", IsEntry: true)],
            []);
        VitrineEvent[] events =
        [
            new(runId, 1, DateTimeOffset.UnixEpoch, TimeSpan.Zero, VitrineEventCategory.Evaluation,
                "gate", VitrineEventDisposition.Succeeded, "subject", "gate", "operation",
                "Synthetic event", "safe event detail", "safe event payload"),
        ];
        return VitrineArtifactSerializer.Create(new VitrineRunOutcome(
            runId,
            new(VitrineRunMode.Evals, Personas.NadiaUserId),
            graph,
            events,
            Evaluation: new SuiteResult([gate], [control])));
    }

    private static void AssertValueBlindRejection(
        Func<object?> action,
        string forbiddenValue,
        string expectedMessage = "Artifact contains disallowed secret-bearing content.")
    {
        var error = Assert.Throws<InvalidDataException>(action);
        Assert.Equal(expectedMessage, error.Message);
        Assert.DoesNotContain(forbiddenValue, error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
