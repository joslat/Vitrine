// SPDX-License-Identifier: MIT
// Copyright (c) 2026 José Luis Latorre Millas

using Galaxus.RecommendationAgent;
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Providers;

namespace AgentEval.VitrineDemo.Tests;

/// <summary>
/// Host selection, the fail-closed contract, and the safety of every configuration value that
/// reaches an operator surface.
/// </summary>
/// <remarks>
/// Most of these use the delegate overload of the resolver and mutate nothing process-wide. The
/// few that must exercise <see cref="Config"/> itself take a <see cref="ProviderEnvironmentScope"/>.
/// </remarks>
public sealed class InferenceProviderSelectionTests
{
    private static InferenceProviderSettings Resolve(params (string Name, string Value)[] variables)
    {
        var map = variables.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.Ordinal);
        return InferenceProviderEnvironment.Resolve(name => map.GetValueOrDefault(name));
    }

    [Fact]
    public void BitdeerNeedsOneVariableAndDefaultsItsEndpointAndModel()
    {
        var settings = Resolve(("AI_INFERENCE_PROVIDER", "bitdeer"), ("BITDEER_API_KEY", "bitdeer-key"));

        Assert.True(settings.IsConfigured);
        Assert.Equal(InferenceProvider.Bitdeer, settings.Provider);
        Assert.Equal("bitdeer", settings.ProviderTag);
        Assert.Equal(InferenceProviderSelection.Explicit, settings.Selection);
        Assert.Equal(InferenceProviderEnvironment.BitdeerDefaultEndpoint, settings.RawEndpoint);
        Assert.Equal(InferenceProviderEnvironment.BitdeerDefaultModel, settings.Model);
        Assert.False(settings.UsesAzureProtocol);
        Assert.Equal($"{InferenceProviderEnvironment.BitdeerDefaultModel}@bitdeer", settings.ModelIdentity);
    }

    [Fact]
    public void BitdeerModelAndEndpointAreOverridable()
    {
        var settings = Resolve(
            ("AI_INFERENCE_PROVIDER", "bitdeer"),
            ("BITDEER_API_KEY", "bitdeer-key"),
            ("BITDEER_ENDPOINT", "https://private.bitdeer.example.invalid/v1"),
            ("BITDEER_MODEL", "zai-org/GLM-5.3-Air"),
            ("BITDEER_MODEL_2", "deepseek-ai/DeepSeek-V3"),
            ("BITDEER_JUDGE_MODEL", "zai-org/GLM-5.3-Flash"));

        Assert.Equal("https://private.bitdeer.example.invalid/v1", settings.RawEndpoint);
        Assert.Equal("zai-org/GLM-5.3-Air", settings.Model);
        Assert.Equal("deepseek-ai/DeepSeek-V3", settings.SecondaryModel);
        Assert.Equal("zai-org/GLM-5.3-Flash", settings.JudgeModel);
        // A host that defines no third model falls back to the primary rather than to a surprise.
        Assert.Equal("zai-org/GLM-5.3-Air", settings.TertiaryModel);
    }

    [Fact]
    public void AnExplicitlyNamedHostWithMissingVariablesFailsClosedAndNeverFallsBack()
    {
        var settings = Resolve(
            ("AI_INFERENCE_PROVIDER", "bitdeer"),
            // A complete Azure setup is present and must NOT be used: running a paid benchmark on
            // the wrong host is worse than not running it.
            ("AZURE_OPENAI_ENDPOINT", "https://resource.example.invalid/"),
            ("AZURE_OPENAI_API_KEY", "azure-key"));

        Assert.False(settings.IsConfigured);
        Assert.Equal(InferenceProvider.None, settings.Provider);
        Assert.Contains("BITDEER_API_KEY", settings.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("azure-key", settings.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownSelectorValueFailsClosedAndListsTheAcceptedValues()
    {
        var settings = Resolve(
            ("AI_INFERENCE_PROVIDER", "whatever-is-cheapest"),
            ("BITDEER_API_KEY", "bitdeer-key"));

        Assert.False(settings.IsConfigured);
        foreach (var known in InferenceProviderEnvironment.KnownValues)
            Assert.Contains(known, settings.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void AzureIsAutoDetectedFirstSoAnExistingMachineIsUnaffected()
    {
        var settings = Resolve(
            ("AZURE_OPENAI_ENDPOINT", "https://resource.example.invalid/"),
            ("AZURE_OPENAI_API_KEY", "azure-key"),
            ("BITDEER_API_KEY", "bitdeer-key"),
            ("OPENAI_API_KEY", "openai-key"));

        Assert.Equal(InferenceProvider.AzureOpenAI, settings.Provider);
        Assert.Equal(InferenceProviderSelection.AutoDetected, settings.Selection);
        Assert.True(settings.UsesAzureProtocol);
    }

    [Fact]
    public void BitdeerIsAutoDetectedWhenOnlyItsKeyIsPresent()
    {
        var settings = Resolve(("BITDEER_API_KEY", "bitdeer-key"));

        Assert.Equal(InferenceProvider.Bitdeer, settings.Provider);
        Assert.Equal(InferenceProviderSelection.AutoDetected, settings.Selection);
    }

    [Fact]
    public void AzureAutoDetectsWithoutAKeyWhenAnEntraModeIsSelected()
    {
        var settings = Resolve(
            ("AZURE_OPENAI_ENDPOINT", "https://resource.example.invalid/"),
            ("AZURE_OPENAI_AUTH_MODE", "managed-identity"));

        Assert.Equal(InferenceProvider.AzureOpenAI, settings.Provider);
    }

    [Fact]
    public void NothingConfiguredNamesEveryHostAndItsRequirements()
    {
        var settings = Resolve();

        Assert.False(settings.IsConfigured);
        Assert.Contains("AI_INFERENCE_PROVIDER is not set", settings.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("BITDEER_API_KEY", settings.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void AHalfConfiguredHostReportsOnlyWhatThatHostLacks()
    {
        var settings = Resolve(("FOUNDRY_ENDPOINT", "https://resource.example.invalid/"));

        Assert.False(settings.IsConfigured);
        Assert.Contains("FOUNDRY_API_KEY", settings.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("FOUNDRY_MODEL", settings.Diagnostic, StringComparison.Ordinal);
        // The operator has just set this one; naming it as missing is how they stop trusting the message.
        Assert.DoesNotContain("AZURE_OPENAI_ENDPOINT", settings.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeylessLocalHostIsOrdinaryAndGetsTheSentinel()
    {
        var settings = Resolve(
            ("OPENAI_COMPATIBLE_ENDPOINT", "http://localhost:11434/v1"),
            ("OPENAI_COMPATIBLE_MODEL", "llama3.3:70b"));

        Assert.Equal(InferenceProvider.OpenAiCompatible, settings.Provider);
        Assert.Equal(InferenceProviderEnvironment.KeylessSentinel, settings.ApiKey);
    }

    [Fact]
    public void TheSettingsStringNeverPrintsTheKeyOrTheEndpointPath()
    {
        var settings = Resolve(
            ("AI_INFERENCE_PROVIDER", "bitdeer"),
            ("BITDEER_API_KEY", "bitdeer-secret-that-must-not-appear"),
            ("BITDEER_ENDPOINT", "https://host.example.invalid/v1/token-in-path"));

        var text = settings.ToString();

        Assert.DoesNotContain("bitdeer-secret-that-must-not-appear", text, StringComparison.Ordinal);
        Assert.DoesNotContain("token-in-path", text, StringComparison.Ordinal);
        Assert.Contains("[redacted]", text, StringComparison.Ordinal);
    }

    [Theory]
    // A configured URL can carry a credential in all four of these places. A guarantee that covers
    // three of them is not a guarantee.
    [InlineData("https://token@host.example.invalid/v1", "token")]
    [InlineData("https://host.example.invalid/v1/sk-token-in-a-path-segment", "sk-token-in-a-path-segment")]
    [InlineData("https://host.example.invalid/v1?api-key=token-in-query", "token-in-query")]
    [InlineData("https://host.example.invalid/v1#token-in-fragment", "token-in-fragment")]
    public void SafeEndpointKeepsSchemeHostAndPortOnly(string endpoint, string secret)
    {
        var safe = InferenceProviderEnvironment.SafeEndpointText(endpoint);

        Assert.DoesNotContain(secret, safe, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("https://host.example.invalid", safe);
    }

    [Fact]
    public void SafeEndpointKeepsANonDefaultPort() =>
        Assert.Equal("http://localhost:11434", InferenceProviderEnvironment.SafeEndpointText("http://localhost:11434/v1"));

    [Theory]
    [InlineData("ftp://host.example.invalid/v1")]
    [InlineData("not-a-url")]
    [InlineData("/relative/v1")]
    public void AnEndpointThatIsNotAnAbsoluteHttpUrlIsRefused(string endpoint)
    {
        Assert.False(InferenceProviderEnvironment.TryValidateEndpoint(endpoint, out _, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void PlainHttpIsRefusedOffLoopbackAndAllowedOnIt()
    {
        Assert.False(InferenceProviderEnvironment.TryValidateEndpoint(
            "http://remote.example.invalid/v1", out _, out var reason));
        Assert.Contains("cleartext", reason, StringComparison.Ordinal);

        Assert.True(InferenceProviderEnvironment.TryValidateEndpoint(
            "http://127.0.0.1:1234/v1", out var loopback, out _));
        Assert.NotNull(loopback);
    }

    [Fact]
    public void AnyConfigurationAttemptedCoversEveryVariableTheResolverReads()
    {
        foreach (var name in InferenceProviderEnvironment.AllVariables)
        {
            Assert.True(
                InferenceProviderEnvironment.AnyConfigurationAttempted(
                    candidate => string.Equals(candidate, name, StringComparison.Ordinal) ? "set" : null),
                $"{name} is read by the resolver but does not count as configuration.");
        }

        Assert.False(InferenceProviderEnvironment.AnyConfigurationAttempted(static _ => null));
    }

    [Fact]
    public void EveryVariableNamedByTheTableIsAlsoInTheScrubbedList()
    {
        foreach (var provider in Enum.GetValues<InferenceProvider>().Where(p => p != InferenceProvider.None))
        {
            Assert.Contains(InferenceProviderEnvironment.EndpointVariableOf(provider)!, InferenceProviderEnvironment.AllVariables);
            Assert.Contains(InferenceProviderEnvironment.ApiKeyVariableOf(provider)!, InferenceProviderEnvironment.AllVariables);
            Assert.Contains(InferenceProviderEnvironment.ModelVariableOf(provider)!, InferenceProviderEnvironment.AllVariables);
            Assert.Contains(InferenceProviderEnvironment.JudgeVariableOf(provider)!, InferenceProviderEnvironment.AllVariables);
            Assert.Contains(InferenceProviderEnvironment.EmbeddingVariableOf(provider)!, InferenceProviderEnvironment.AllVariables);
        }
    }

    [Fact]
    public void EverySecretBearingVariableIsAKeyOrAnEndpoint()
    {
        var names = InferenceProviderEnvironment.SecretBearingVariables.Select(v => v.Name).ToList();

        Assert.Contains("BITDEER_API_KEY", names);
        Assert.Contains("BITDEER_ENDPOINT", names);
        Assert.Contains("AZURE_OPENAI_API_KEY", names);
        Assert.Contains("AZURE_OPENAI_MANAGED_IDENTITY_CLIENT_ID", names);
        Assert.All(names, name => Assert.Contains(name, InferenceProviderEnvironment.AllVariables));
    }

    // ── Through Config, which applies the family-specific validation ──────────────────────────

    [Fact]
    public void BitdeerResolvesThroughConfigAndBuildsAChatClientWithoutContactingTheHost()
    {
        using var environment = new ProviderEnvironmentScope();
        environment.Set("AI_INFERENCE_PROVIDER", "bitdeer");
        environment.Set("BITDEER_API_KEY", "bitdeer-secret-that-must-not-appear");

        var readiness = Config.Readiness;

        Assert.True(readiness.IsReady);
        Assert.Equal("bitdeer", readiness.ProviderTag);
        Assert.Equal("Bitdeer AI Model Studio", readiness.ProviderDisplayName);
        Assert.Equal(InferenceProviderEnvironment.BitdeerDefaultModel, Config.Model);
        Assert.Equal(InferenceProviderEnvironment.BitdeerDefaultModel, Config.JudgeDeployment);
        Assert.Equal($"{InferenceProviderEnvironment.BitdeerDefaultModel}@bitdeer", Config.ModelIdentity);
        Assert.DoesNotContain("bitdeer-secret-that-must-not-appear", readiness.SafeSummary, StringComparison.Ordinal);
        Assert.Contains("provider not contacted", readiness.SafeSummary, StringComparison.Ordinal);

        using var chatClient = RecommendationAgentFactory.CreateConfiguredChatClient();
        Assert.NotNull(chatClient);
    }

    [Fact]
    public void ANamespacedModelSurvivesTheLabelAndTheNamePolicy()
    {
        using var environment = new ProviderEnvironmentScope();
        environment.Set("AI_INFERENCE_PROVIDER", "bitdeer");
        environment.Set("BITDEER_API_KEY", "bitdeer-key");

        // A slash is what distinguishes zai-org/GLM-5.3-Flash from a name the host cannot route.
        Assert.Equal(InferenceProviderEnvironment.BitdeerDefaultModel, Config.Deployments.SubjectLabel);
        Assert.True(Config.IsValidModelName("zai-org/GLM-5.3-Flash"));
        Assert.True(Config.IsValidModelName("llama3.3:70b"));

        // …and it is still not a path the caller may walk out of.
        Assert.False(Config.IsValidModelName("../../secrets"));
        Assert.False(Config.IsValidModelName("/leading-slash"));
        Assert.False(Config.IsValidModelName("zai-org//GLM"));

        // The Azure protocol keeps the stricter policy, because there the name IS a path segment.
        Assert.False(Config.IsValidDeploymentName("zai-org/GLM-5.3-Flash"));
    }

    [Fact]
    public void ABitdeerEndpointOnPlainHttpToARemoteHostFailsClosed()
    {
        using var environment = new ProviderEnvironmentScope();
        environment.Set("AI_INFERENCE_PROVIDER", "bitdeer");
        environment.Set("BITDEER_API_KEY", "bitdeer-key");
        environment.Set("BITDEER_ENDPOINT", "http://api-inference.bitdeer.example.invalid/v1");

        var readiness = Config.Readiness;

        Assert.False(readiness.IsReady);
        Assert.Contains("BITDEER_ENDPOINT", readiness.BlockingReason, StringComparison.Ordinal);
        Assert.Contains("cleartext", readiness.BlockingReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecretShapedBitdeerModelIsRejectedAndRedacted()
    {
        const string accidentalSecret = "sk-accidental-secret-in-a-model-name-123456";
        using var environment = new ProviderEnvironmentScope();
        environment.Set("AI_INFERENCE_PROVIDER", "bitdeer");
        environment.Set("BITDEER_API_KEY", "bitdeer-key");
        environment.Set("BITDEER_MODEL", accidentalSecret);

        var readiness = Config.Readiness;

        Assert.False(readiness.IsReady);
        Assert.Equal("[REDACTED]", readiness.Deployments.SubjectLabel);
        Assert.DoesNotContain(accidentalSecret, readiness.SafeSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBannerPrintsTheHostAndTheModelButNeitherKeyNorEndpoint()
    {
        const string key = "bitdeer-banner-secret-9371";
        const string endpoint = "https://banner.bitdeer.example.invalid/v1";
        using var environment = new ProviderEnvironmentScope();
        environment.Set("AI_INFERENCE_PROVIDER", "bitdeer");
        environment.Set("BITDEER_API_KEY", key);
        environment.Set("BITDEER_ENDPOINT", endpoint);

        using var writer = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(writer);
            Config.PrintProviderTarget();
        }
        finally
        {
            Console.SetOut(previous);
        }

        var text = writer.ToString();
        Assert.Contains("Bitdeer AI Model Studio", text, StringComparison.Ordinal);
        Assert.Contains(InferenceProviderEnvironment.BitdeerDefaultModel, text, StringComparison.Ordinal);
        Assert.DoesNotContain(key, text, StringComparison.Ordinal);
        Assert.DoesNotContain(endpoint, text, StringComparison.Ordinal);
        Assert.DoesNotContain("banner.bitdeer.example.invalid", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ABitdeerKeyIsRedactedFromEveryRuntimeSurface()
    {
        const string key = "BITDEER-RUNTIME-SENTINEL-4471";
        using var environment = new ProviderEnvironmentScope();
        environment.Set("AI_INFERENCE_PROVIDER", "bitdeer");
        environment.Set("BITDEER_API_KEY", key);

        Assert.True(Galaxus.RecommendationAgent.Observability.RecommendationRuntimeEvents
            .ContainsSecretBearingContent($"response enclosed [{key}]"));
        Assert.DoesNotContain(
            key,
            Galaxus.RecommendationAgent.Observability.RecommendationRuntimeEvents
                .SafePreview($"response enclosed [{key}]"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitlyNamedHostThatCannotBeBuiltLeavesConfigurationAttemptedTrue()
    {
        using var environment = new ProviderEnvironmentScope();
        environment.Set("AI_INFERENCE_PROVIDER", "bitdeer");

        Assert.False(Config.IsConfigured);
        // The distinction a degraded path must respect: this machine is misconfigured, not bare.
        Assert.True(InferenceProviderEnvironment.AnyConfigurationAttempted(Environment.GetEnvironmentVariable));
    }

    [Fact]
    public void AnEmptyEnvironmentIsUnconfiguredRatherThanMisconfigured()
    {
        using var environment = new ProviderEnvironmentScope();

        Assert.False(Config.IsConfigured);
        Assert.False(InferenceProviderEnvironment.AnyConfigurationAttempted(Environment.GetEnvironmentVariable));
    }

    [Fact]
    public void TheNetworkTimeoutIsGenerousByDefaultAndBoundedWhenSet()
    {
        Assert.Equal(
            InferenceProviderEnvironment.DefaultNetworkTimeout,
            InferenceProviderEnvironment.NetworkTimeout(static _ => null));
        Assert.Equal(
            TimeSpan.FromSeconds(42),
            InferenceProviderEnvironment.NetworkTimeout(static _ => "42"));
        // Nonsense and out-of-range values fall back rather than disabling the deadline.
        Assert.Equal(
            InferenceProviderEnvironment.DefaultNetworkTimeout,
            InferenceProviderEnvironment.NetworkTimeout(static _ => "0"));
        Assert.Equal(
            InferenceProviderEnvironment.DefaultNetworkTimeout,
            InferenceProviderEnvironment.NetworkTimeout(static _ => "forever"));
    }
}
