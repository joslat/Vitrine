// SPDX-License-Identifier: MIT
// Copyright (c) 2026 José Luis Latorre Millas

using Galaxus.RecommendationAgent;
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Observability;
using AgentEval.VitrineDemo.Evals.Live;

namespace AgentEval.VitrineDemo.Tests;

public sealed class AzureOpenAiConfigurationTests
{
    [Fact]
    public void MissingAuthModeRetainsTheApiKeyCompatibilityPath()
    {
        using var environment = new AzureEnvironmentScope();
        environment.Set("AZURE_OPENAI_ENDPOINT", "https://configuration.example.invalid/");
        environment.Set("AZURE_OPENAI_API_KEY", "secret-that-must-not-appear");
        environment.Set("AZURE_OPENAI_DEPLOYMENT", "subject-deployment");

        var readiness = Config.Readiness;

        Assert.True(readiness.IsReady);
        Assert.Equal(AzureOpenAiAuthenticationMode.ApiKey, readiness.AuthenticationMode);
        Assert.Equal("subject-deployment", readiness.Deployments.SubjectDeployment);
        Assert.Equal("subject-deployment", readiness.Deployments.JudgeDeployment);
        Assert.True(readiness.Deployments.JudgeSharesSubjectDeployment);
        Assert.Contains("compatibility default", readiness.AuthenticationLabel, StringComparison.Ordinal);
        Assert.Contains("provider not contacted", readiness.SafeSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("configuration.example.invalid", readiness.SafeSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-that-must-not-appear", readiness.SafeSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalDefaultCredentialNeedsNoApiKeyAndBuildsWithoutContactingAzure()
    {
        using var environment = new AzureEnvironmentScope();
        environment.Set("AZURE_OPENAI_ENDPOINT", "https://configuration.example.invalid/");
        environment.Set("AZURE_OPENAI_AUTH_MODE", "default-credential");
        environment.Set("AZURE_OPENAI_API_KEY", null);

        var readiness = Config.Readiness;

        Assert.True(readiness.IsReady);
        Assert.Equal(AzureOpenAiAuthenticationMode.DefaultAzureCredential, readiness.AuthenticationMode);
        Assert.Contains("local DefaultAzureCredential", readiness.AuthenticationLabel, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Config.KeyCredential);

        using var chatClient = RecommendationAgentFactory.CreateConfiguredChatClient();
        Assert.NotNull(chatClient);
    }

    [Fact]
    public void ManagedIdentityIsDeterministicAndDoesNotExposeAUserAssignedClientId()
    {
        const string clientId = "2a7a75ef-7f80-43f4-8dbf-c4679fb91fa4";
        using var environment = new AzureEnvironmentScope();
        environment.Set("AZURE_OPENAI_ENDPOINT", "https://managed-identity.example.invalid/");
        environment.Set("AZURE_OPENAI_AUTH_MODE", "managed-identity");
        environment.Set("AZURE_OPENAI_MANAGED_IDENTITY_CLIENT_ID", clientId);
        environment.Set("AZURE_OPENAI_API_KEY", "present-but-never-a-runtime-fallback");

        var readiness = Config.Readiness;

        Assert.True(readiness.IsReady);
        Assert.Equal(AzureOpenAiAuthenticationMode.ManagedIdentity, readiness.AuthenticationMode);
        Assert.Contains("user-assigned", readiness.AuthenticationLabel, StringComparison.Ordinal);
        Assert.DoesNotContain(clientId, readiness.SafeSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("managed-identity.example.invalid", readiness.SafeSummary, StringComparison.Ordinal);
        Assert.True(RecommendationRuntimeEvents.ContainsSecretBearingContent(clientId));
        Assert.Equal("[REDACTED_MANAGED_IDENTITY]", RecommendationRuntimeEvents.SafePreview(clientId));

        using var chatClient = RecommendationAgentFactory.CreateConfiguredChatClient();
        Assert.NotNull(chatClient);
    }

    [Fact]
    public void InvalidManagedIdentityClientIdDoesNotFallBackToAPresentApiKey()
    {
        using var environment = new AzureEnvironmentScope();
        environment.Set("AZURE_OPENAI_ENDPOINT", "https://configuration.example.invalid/");
        environment.Set("AZURE_OPENAI_AUTH_MODE", "managed-identity");
        environment.Set("AZURE_OPENAI_MANAGED_IDENTITY_CLIENT_ID", "not-a-guid");
        environment.Set("AZURE_OPENAI_API_KEY", "must-not-be-used");

        var readiness = Config.Readiness;

        Assert.False(readiness.IsReady);
        Assert.Equal(AzureOpenAiAuthenticationMode.ManagedIdentity, readiness.AuthenticationMode);
        Assert.Contains("must be a user-assigned identity client GUID", readiness.BlockingReason, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => RecommendationAgentFactory.CreateConfiguredChatClient());
    }

    [Fact]
    public void UnrecognizedExplicitAuthModeFailsClosedEvenWhenAKeyExists()
    {
        using var environment = new AzureEnvironmentScope();
        environment.Set("AZURE_OPENAI_ENDPOINT", "https://configuration.example.invalid/");
        environment.Set("AZURE_OPENAI_AUTH_MODE", "try-everything");
        environment.Set("AZURE_OPENAI_API_KEY", "must-not-be-used");

        var readiness = Config.Readiness;

        Assert.False(readiness.IsReady);
        Assert.Null(readiness.AuthenticationMode);
        Assert.Contains("must be api-key, default-credential, or managed-identity", readiness.BlockingReason, StringComparison.Ordinal);
    }

    [Fact]
    public void SeparateJudgeDeploymentIsExplicitAndBuildsThroughTheSharedComposition()
    {
        using var environment = new AzureEnvironmentScope();
        environment.Set("AZURE_OPENAI_ENDPOINT", "https://configuration.example.invalid/");
        environment.Set("AZURE_OPENAI_API_KEY", "secret-that-must-not-appear");
        environment.Set("AZURE_OPENAI_DEPLOYMENT", "subject-deployment");
        environment.Set("AZURE_OPENAI_JUDGE_DEPLOYMENT", "judge-deployment");

        var readiness = Config.Readiness;

        Assert.True(readiness.IsReady);
        Assert.Equal("judge-deployment", readiness.Deployments.JudgeDeployment);
        Assert.Equal("judge-deployment", readiness.Deployments.JudgeLabel);
        Assert.False(readiness.Deployments.JudgeSharesSubjectDeployment);

        using var judgeClient = RecommendationAgentFactory.CreateConfiguredChatClient(Config.JudgeDeployment);
        Assert.NotNull(judgeClient);
    }

    [Fact]
    public void InvalidDeploymentCharactersFailReadinessAndCannotInjectLogLines()
    {
        using var environment = new AzureEnvironmentScope();
        environment.Set("AZURE_OPENAI_ENDPOINT", "https://configuration.example.invalid/");
        environment.Set("AZURE_OPENAI_API_KEY", "secret-that-must-not-appear");
        environment.Set("AZURE_OPENAI_JUDGE_DEPLOYMENT", "judge\r\ninjected");

        var readiness = Config.Readiness;

        Assert.False(readiness.IsReady);
        Assert.Equal("judge??injected", readiness.Deployments.JudgeLabel);
        Assert.DoesNotContain('\r', readiness.SafeSummary);
        Assert.DoesNotContain('\n', readiness.SafeSummary);
        Assert.Contains("unsupported characters", Assert.Throws<ArgumentException>(() =>
            RecommendationAgentFactory.CreateConfiguredChatClient(Config.JudgeDeployment)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EndpointMustBeHttpsForEveryAuthenticationMode()
    {
        using var environment = new AzureEnvironmentScope();
        environment.Set("AZURE_OPENAI_ENDPOINT", "http://configuration.example.invalid/");
        environment.Set("AZURE_OPENAI_AUTH_MODE", "default-credential");

        var readiness = Config.Readiness;

        Assert.False(readiness.IsReady);
        Assert.Contains("absolute HTTPS", readiness.BlockingReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://resource.services.ai.azure.com/api/projects/project-name")]
    [InlineData("https://resource.services.ai.azure.com/API/PROJECTS/project-name/")]
    [InlineData("https://resource.services.ai.azure.com/api%2Fprojects%2Fproject-name")]
    public void FoundryProjectEndpointIsRejectedBeforeAnyProviderCall(string endpoint)
    {
        using var environment = new AzureEnvironmentScope();
        environment.Set("AZURE_OPENAI_ENDPOINT", endpoint);
        environment.Set("AZURE_OPENAI_AUTH_MODE", "default-credential");

        var readiness = Config.Readiness;

        Assert.False(readiness.IsReady);
        Assert.Contains("not a Microsoft Foundry project endpoint", readiness.BlockingReason,
            StringComparison.Ordinal);
        Assert.Contains("provider not contacted", readiness.SafeSummary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://portal.azure.com/#view/example")]
    [InlineData("https://configuration.example.invalid/openai/deployments")]
    [InlineData("https://configuration.example.invalid/?api-version=preview")]
    [InlineData("https://user@configuration.example.invalid/")]
    public void EndpointMustBeTheResourceBaseUri(string endpoint)
    {
        using var environment = new AzureEnvironmentScope();
        environment.Set("AZURE_OPENAI_ENDPOINT", endpoint);
        environment.Set("AZURE_OPENAI_AUTH_MODE", "default-credential");

        var readiness = Config.Readiness;

        Assert.False(readiness.IsReady);
        Assert.Contains("base Azure OpenAI resource/inference endpoint", readiness.BlockingReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SecretShapedDeploymentIsRejectedAndRedactedFromEverySafeSurface()
    {
        const string accidentalSecret = "sk-accidental-secret-in-deployment-123456";
        using var environment = new AzureEnvironmentScope();
        environment.Set("AZURE_OPENAI_ENDPOINT", "https://configuration.example.invalid/");
        environment.Set("AZURE_OPENAI_API_KEY", "different-configured-key");
        environment.Set("AZURE_OPENAI_DEPLOYMENT", accidentalSecret);

        var readiness = Config.Readiness;

        Assert.False(readiness.IsReady);
        Assert.Equal("[REDACTED]", readiness.Deployments.SubjectLabel);
        Assert.DoesNotContain(accidentalSecret, readiness.SafeSummary, StringComparison.Ordinal);
        Assert.Contains("credential-shaped content", readiness.BlockingReason, StringComparison.Ordinal);

        using var writer = new StringWriter();
        var originalWriter = Console.Out;
        try
        {
            Console.SetOut(writer);
            Config.PrintAzureTarget();
        }
        finally
        {
            Console.SetOut(originalWriter);
        }

        Assert.DoesNotContain(accidentalSecret, writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", writer.ToString(), StringComparison.Ordinal);
        Assert.Equal("[REDACTED]", new AgentEvalRedTeamSafetyEvaluator().ModelId);
    }

    private sealed class AzureEnvironmentScope : IDisposable
    {
        private static readonly string[] Names =
        [
            "AZURE_OPENAI_ENDPOINT",
            "AZURE_OPENAI_API_KEY",
            "AZURE_OPENAI_AUTH_MODE",
            "AZURE_OPENAI_MANAGED_IDENTITY_CLIENT_ID",
            "AZURE_OPENAI_DEPLOYMENT",
            "AZURE_OPENAI_JUDGE_DEPLOYMENT",
            "AZURE_OPENAI_EMBEDDING_DEPLOYMENT"
        ];

        private readonly IReadOnlyDictionary<string, string?> _original =
            Names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        private readonly string? _originalModelOverride = Config.ModelOverride;
        private readonly string? _originalEmbeddingOverride = Config.EmbeddingModelOverride;

        public AzureEnvironmentScope()
        {
            foreach (var name in Names)
                Environment.SetEnvironmentVariable(name, null);
            Config.ModelOverride = null;
            Config.EmbeddingModelOverride = null;
        }

        public void Set(string name, string? value) => Environment.SetEnvironmentVariable(name, value);

        public void Dispose()
        {
            foreach (var pair in _original)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            Config.ModelOverride = _originalModelOverride;
            Config.EmbeddingModelOverride = _originalEmbeddingOverride;
        }
    }
}
