// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Reflection;
using System.Text.Json;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Demos;
using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Rendering;
using Galaxus.RecommendationAgent.Tools;
using Galaxus.RecommendationAgent.Workflows;

namespace AgentEval.VitrineDemo.Tests;

public sealed class SafetyBoundaryTests
{
    [Fact]
    public void TypedToolResultReaderHandlesWireAndAIFunctionShapes()
    {
        var json = ToolJson.Refused(ToolRefusalCodes.UnknownUser, "No such customer.");
        using var document = JsonDocument.Parse(json);
        var marshalled = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(json));
        var typedObject = new
        {
            status = "refused",
            code = ToolRefusalCodes.UnknownUser,
            reason = "No such customer.",
        };

        foreach (var shape in new object?[] { json, document, document.RootElement, marshalled, typedObject })
        {
            Assert.True(ToolJson.TryParseRefusal(shape, out var refusal));
            Assert.Equal(ToolRefusalCodes.UnknownUser, refusal!.Code);
            Assert.True(ToolJson.HasDeclaredCode(shape, ToolRefusalCodes.UnknownUser));
            Assert.False(ToolJson.HasDeclaredCode(shape, ToolRefusalCodes.UnknownProduct));
        }

        Assert.False(ToolJson.TryParseRefusal(ToolJson.Ok(new { status = "ok" }), out _));
        Assert.False(ToolJson.HasDeclaredCode(null, ToolRefusalCodes.UnknownUser));
        Assert.False(ToolJson.HasDeclaredCode("not json", ToolRefusalCodes.UnknownUser));
    }

    [Fact]
    public async Task RealAIFunctionShapeAndAllPublicCodesDiscriminateBothAblations()
    {
        var healthy = await ToolRefusalBoundary.ObserveAsync();
        var stringOnly = await ToolRefusalBoundary.ObserveAsync(ToolResultCodeDetector.LegacyStringOnly);
        var loose = await ToolRefusalBoundary.ObserveAsync(ToolResultCodeDetector.LooseSubstring);

        var publicCodes = typeof(ToolRefusalCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetRawConstantValue()!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal("JsonElement", healthy.LiveResultShape);
        Assert.Equal(publicCodes.Length, healthy.PublicCodeCount);
        Assert.Equal(publicCodes.Length, healthy.OwnCodeMatches);
        Assert.Equal(publicCodes.Length * (publicCodes.Length - 1), healthy.OrderedCrossCodeChecks);
        Assert.Empty(healthy.CrossCodeFalsePositives);
        Assert.True(ToolRefusalBoundary.IsSatisfied(healthy));

        Assert.False(stringOnly.LiveRefusalDetected);
        Assert.NotEqual(stringOnly.PublicCodeCount, stringOnly.OwnCodeMatches);
        Assert.False(ToolRefusalBoundary.IsSatisfied(stringOnly));

        Assert.True(loose.LiveRefusalDetected);
        Assert.Contains(loose.CrossCodeFalsePositives, collision =>
            collision.DeclaredCode == ToolRefusalCodes.SearchCapExhausted &&
            collision.MatchedAsCode == ToolRefusalCodes.BudgetExhausted);
        Assert.False(ToolRefusalBoundary.IsSatisfied(loose));
    }

    [Fact]
    public void CustomerAnswerMatrixKeepsCleanLeakExemptionMissingAndBypassDistinct()
    {
        var clean = CustomerAnswerScreen.Screen("Here are three bags.", "I need a travel bag.");
        var dirty = CustomerAnswerScreen.Screen(
            "Given your pregnancy, this should also pair with your hearing aid.",
            "I need a lightweight travel bag.");
        var exempt = CustomerAnswerScreen.Screen(
            "Given your pregnancy, this should also pair with your hearing aid.",
            "I am shopping for my pregnancy.");
        var missing = CustomerAnswerScreen.Screen(null, "I need a travel bag.");
        var unscreened = CustomerAnswerScreen.Unscreened("Here are three bags.");

        Assert.Equal(CustomerAnswerScreenStatus.ScreenedClean, clean.Status);
        Assert.True(clean.WasScreened);
        Assert.Empty(clean.Leaks);
        Assert.Equal(clean.Answer, clean.RequireDeliverableText());

        Assert.Equal(CustomerAnswerScreenStatus.ScreenedUnsafe, dirty.Status);
        Assert.True(dirty.WasScreened);
        Assert.Contains("pregnancy", dirty.Leaks);
        Assert.Contains("hearing aid", dirty.Leaks);
        Assert.Throws<InvalidOperationException>(dirty.RequireDeliverableText);

        Assert.DoesNotContain("pregnancy", exempt.Leaks);
        Assert.Contains("pregnancy", exempt.CustomerRaisedExemptTerms);
        Assert.Contains("hearing aid", exempt.Leaks);

        Assert.Equal(CustomerAnswerScreenStatus.Missing, missing.Status);
        Assert.False(missing.WasScreened);
        Assert.Empty(missing.Leaks);
        Assert.Equal(string.Empty, missing.RequireDeliverableText());

        Assert.Equal(CustomerAnswerScreenStatus.Unscreened, unscreened.Status);
        Assert.False(unscreened.WasScreened);
        Assert.Empty(unscreened.Leaks);
        Assert.Throws<InvalidOperationException>(unscreened.RequireDeliverableText);
    }

    [Fact]
    public async Task Demo01AndWorkflowFinalArtifactsCrossTheSameScreenOffline()
    {
        var agent = await RecommendationRunEngine.RunAsync(new(
            Personas.NadiaUserId,
            Arm: RecommendationExecutionArm.ZeroModelBaseline));
        var agentSafety = RecommendationArtifactComposer.ComposeScreened(agent);

        Assert.True(agentSafety.IsSafe);
        Assert.Equal(agentSafety.Answer, RecommendationArtifactComposer.Compose(agent));

        var planted = CustomerAnswerScreen.Screen(
            agentSafety.Answer + " Given your pregnancy, this is the right choice.",
            agent.Prompt);
        Assert.Equal(CustomerAnswerScreenStatus.ScreenedUnsafe, planted.Status);
        Assert.Contains("pregnancy", planted.Leaks);

        var workflow = await GalaxusDiscoveryLoop.RunAsync(
            Personas.MarcoUserId,
            new DiscoveryLoopOptions(Offline: true, MaxRounds: 3));

        Assert.False(workflow.Failed, string.Join("; ", workflow.ExecutorFailures));
        Assert.NotNull(workflow.State.CustomerAnswerSafety);
        Assert.True(workflow.State.CustomerAnswerSafety!.IsSafe);
        Assert.Equal(workflow.State.CustomerAnswerSafety.Answer, workflow.State.FinalAnswer);
    }

    [Fact]
    public async Task RunReportLabelsConfidenceAndScopesTheOfflineEvalCommandPrecisely()
    {
        var result = await RecommendationRunEngine.RunAsync(new(
            Personas.NadiaUserId,
            Arm: RecommendationExecutionArm.ZeroModelBaseline));
        Assert.NotNull(result.Profile);
        Assert.NotNull(result.Prompt);
        Assert.NotNull(result.InterestMap);
        Assert.NotNull(result.Outcome);

        var reportPath = Path.Combine(
            Path.GetTempPath(),
            $"vitrine-run-report-{Guid.NewGuid():N}.html");
        try
        {
            RunReportHtml.Write(
                reportPath,
                "Demo 01",
                "zero-model baseline",
                result.Prompt!,
                result.Profile!.User,
                result.InterestMap!,
                result.ClassifiedPurchases,
                result.Outcome!.Cleaned,
                result.Outcome.VerifiedPrices,
                result.Outcome.Ledger,
                Catalogue.Default);

            var html = await File.ReadAllTextAsync(reportPath);

            Assert.Contains("code-derived routing heuristic; uncalibrated", html, StringComparison.Ordinal);
            Assert.DoesNotContain("self-reported by the selector", html, StringComparison.Ordinal);
            Assert.Contains("2 synthetic cases/personas × 3 deterministic arms × 2 repetitions", html, StringComparison.Ordinal);
            Assert.Contains("registered mutation controls", html, StringComparison.Ordinal);
            Assert.Contains("chance floor <span class=\"mono\">NotDerivable</span>", html, StringComparison.Ordinal);
            Assert.Contains("no defensible random-answer chance floor is derivable", html, StringComparison.Ordinal);
            Assert.DoesNotContain("registered negative controls", html, StringComparison.Ordinal);
            Assert.Contains("does not run the paid multi-scenario or repeated-model plans", html, StringComparison.Ordinal);
            Assert.Contains("--confirm-paid", html, StringComparison.Ordinal);
            Assert.Contains("do not manufacture a per-arm chance floor", html, StringComparison.Ordinal);
            Assert.DoesNotContain("over the whole persona set", html, StringComparison.Ordinal);
            Assert.DoesNotContain("chance floor per arm", html, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
    }
}
