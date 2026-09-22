// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.Evals;
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Workflows;

namespace AgentEval.VitrineDemo.Tests;

public sealed class VitrineContractTests
{
    [Fact]
    public void CrownJewelCardinalitiesAreExact()
    {
        Assert.Equal(99, Catalogue.Default.All.Count);
        Assert.Equal(14, Personas.AllPersonaIds.Count);
        Assert.Equal(13, RecommendationAgentFactory.BuildReadOnlyTools().Length);
        Assert.Equal(2, RecommendationAgentFactory.BuildApprovalGatedCommitTools().Length);
        Assert.Equal(15,
            RecommendationAgentFactory.BuildReadOnlyTools().Length +
            RecommendationAgentFactory.BuildApprovalGatedCommitTools().Length);
    }

    [Fact]
    public void EveryProductionCheckIsAUniqueAgentEvalAtomicCodeEvalWithANativeFloor()
    {
        Assert.Equal(6, VitrineProductionChecks.All.Count);
        Assert.Equal(6, VitrineProductionChecks.All.Select(static check => check.Key)
            .Distinct(StringComparer.Ordinal).Count());
        Assert.All(VitrineProductionChecks.All, check =>
        {
            Assert.IsAssignableFrom<AgentEval.Evals.AtomicCodeEval>(check.EvalFactory());
            Assert.False(string.IsNullOrWhiteSpace(check.Floor.Derivation));
        });
    }

    [Fact]
    public async Task AllFortyThreeNegativeControlsFailThenRecover()
    {
        var controls = await NegativeControlRunner.RunAsync();

        Assert.Equal(43, controls.Count);
        Assert.Equal(43, controls.Select(control => control.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(controls, control =>
        {
            Assert.Equal(ControlAttemptOutcome.MeasuredPass, control.HealthyOutcome);
            Assert.True(control.BrokenWentRed, control.Name);
            Assert.True(control.RestoredWentGreen, control.Name);
            Assert.True(control.Caught, control.Name);
            Assert.NotEqual("unspecified", control.Target);
            Assert.NotEqual("unspecified", control.ObservationProducer);
            Assert.NotEqual("unspecified", control.Evaluator);
            Assert.Contains(control.Tranche, new[] { "E02A", "E02B", "E02C" });
            Assert.True(Enum.IsDefined(control.ScopeClass));
        });
        Assert.Equal(20, controls.Count(static control => control.ScopeClass == ControlScopeClass.ProductionObservation));
        Assert.Equal(23, controls.Count(static control => control.ScopeClass == ControlScopeClass.BoundaryCalibrationFixture));
    }

    [Fact]
    public async Task NegativeControlsExerciseConcreteArtifactsAndContainFaults()
    {
        var controls = await NegativeControlRunner.RunAsync();

        Assert.Contains("GLX-9999", controls.Single(control => control.Name == "Hallucinator").Evidence, StringComparison.Ordinal);
        var commitOrdering = controls.Single(control => control.Name == "CommitOrdering");
        Assert.True(commitOrdering.Caught);
        Assert.Equal("ToolCallBudget.HasGroundedCommitOrder", commitOrdering.Target);
        Assert.Contains("GetProductDetails", commitOrdering.Evidence, StringComparison.Ordinal);
        Assert.Contains("PlaceOrder", commitOrdering.Evidence, StringComparison.Ordinal);
        Assert.Contains("subject supplied pass/fail=True", controls.Single(control => control.Name == "EverySnapshotSaysWhatProducedIt").Evidence, StringComparison.Ordinal);
        Assert.Contains("contained ExpectedControlPlantException", controls.Single(control => control.Name == "EveryControlRowIsContained").Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullOfflineChainPassesAndCatalogueAblationFailsClosed()
    {
        using var writer = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(writer);
            var healthy = await EvaluationSuite.RunAsync();
            var ablated = await EvaluationSuite.RunAsync(leaveCatalogueAblated: true);

            Assert.Equal(0, healthy.ExitCode);
            Assert.Equal(43, healthy.CaughtControls);
            Assert.Equal(1, ablated.ExitCode);
            Assert.False(ablated.Gates.Single(gate => gate.Name.StartsWith("Catalogue", StringComparison.Ordinal)).Passed);
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    [Fact]
    public async Task MissingMeasurementRendersAsNotMeasuredNeverZero()
    {
        var result = new SuiteResult(
            [GateResult.NotMeasured("absent", AgentEval.Evals.Meta.ChanceFloor.NotDerivable(
                "this synthetic missing observation has no random-response population"), "no observation")],
            []);
        using var writer = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(writer);
            ConsoleReport.Print(result, verboseControls: false);
        }
        finally
        {
            Console.SetOut(previous);
        }

        var text = writer.ToString();
        Assert.Contains("NOT MEASURED", text, StringComparison.Ordinal);
        Assert.Contains("score —", text, StringComparison.Ordinal);
        Assert.DoesNotContain("score 0.000", text, StringComparison.Ordinal);
        Assert.Equal(3, result.ExitCode);

        var json = EvaluationReportJson.Render(result);
        var html = EvaluationReportHtml.Render(result);
        Assert.Contains("\"score\": null", json, StringComparison.Ordinal);
        Assert.Contains("NOT MEASURED", html, StringComparison.Ordinal);
        Assert.Contains("—", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">0.000<", html, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public void WorkflowProgressNeverRendersMissingElapsedTimeAsZero()
    {
        using var writer = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(writer);
            var sink = new ConsoleDiscoveryProgressSink();
            sink.Publish(new(DiscoveryEventKind.NodeCompleted, "Presenter", string.Empty, ModelCalls: 1));
            sink.Publish(new(DiscoveryEventKind.RunComplete, string.Empty, "complete"));
        }
        finally
        {
            Console.SetOut(previous);
        }

        var text = writer.ToString();
        Assert.Equal(2, text.Split("NOT MEASURED", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("0.00 s", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HashBoundariesRejectConfiguredSecretsBeforeDigesting()
    {
        // Scrub EVERY provider variable: an ambient key for any host would otherwise
        // auto-detect into this test and change what it is asserting on.
        using var providerEnvironment = new ProviderEnvironmentScope();
        const string key = "SENTINEL-UNLABELLED-VITRINE-KEY-9371";
        const string endpoint = "https://sentinel-prehash.example.invalid/openai";
        var oldKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var oldEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var path = Path.Combine(Path.GetTempPath(), $"vitrine-secret-evidence-{Guid.NewGuid():N}.json");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", key);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", endpoint);
            Assert.Throws<InvalidDataException>(() => EmbeddingDocument.HashQuery($"customer text [{key}]"));
            Assert.Throws<InvalidDataException>(() => EmbeddingDocument.HashQuery($"customer text [{endpoint}]"));
            File.WriteAllText(path, $"{{\"evidence\":\"prefix-{key}-suffix\"}}");
            Assert.Equal("EvidenceContainsDisallowedContent", HonestyEvidenceLoader.Load(path).FailureKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", oldKey);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", oldEndpoint);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CredentialBannerPrintsDeploymentOnly()
    {
        // Scrub EVERY provider variable: an ambient key for any host would otherwise
        // auto-detect into this test and change what it is asserting on.
        using var providerEnvironment = new ProviderEnvironmentScope();
        const string endpoint = "https://synthetic-vitrine-resource.example.invalid/";
        const string key = "sk-SYNTHETIC-VITRINE-KEY-DO-NOT-USE-123456789";
        const string deployment = "vitrine-test-deployment";
        var oldEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var oldKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var oldDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
        using var writer = new StringWriter();
        var previous = Console.Out;
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", endpoint);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", key);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", deployment);
            Console.SetOut(writer);
            Galaxus.RecommendationAgent.Config.PrintProviderTarget();
        }
        finally
        {
            Console.SetOut(previous);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", oldEndpoint);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", oldKey);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", oldDeployment);
        }

        var text = writer.ToString();
        Assert.Contains(deployment, text, StringComparison.Ordinal);
        Assert.DoesNotContain(endpoint, text, StringComparison.Ordinal);
        Assert.DoesNotContain(key, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Endpoint", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NextPurchaseDesignCannotReachFivePercentSignificance()
    {
        Assert.Equal(0.125, Math.Min(1.0, 2.0 * Math.Pow(0.5, 4)), 12);
    }

    [Fact]
    public void CentralPackageManagementHasNoProjectVersionAttributes()
    {
        var root = FindRepositoryRoot();
        var projects = RepositorySourceFiles(root, "*.csproj");
        Assert.All(projects, project => Assert.DoesNotContain(" Version=", File.ReadAllText(project), StringComparison.Ordinal));
    }

    [Fact]
    public void EveryCSharpFileHasSpdxHeader()
    {
        var root = FindRepositoryRoot();
        var sources = RepositorySourceFiles(root, "*.cs");
        Assert.All(sources, source => Assert.StartsWith("// SPDX-License-Identifier: MIT", File.ReadAllText(source), StringComparison.Ordinal));
    }

    private static IEnumerable<string> RepositorySourceFiles(string root, string pattern) =>
        new[] { "src", "tests" }
            .SelectMany(folder => Directory.EnumerateFiles(
                Path.Combine(root, folder), pattern, SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("obj", StringComparison.OrdinalIgnoreCase)));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgentEval.VitrineDemo.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate VITRINE repository root.");
    }
}
