// SPDX-License-Identifier: MIT
// Copyright (c) 2026 José Luis Latorre Millas

using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace Galaxus.RecommendationAgent.Providers;

/// <summary>
/// The single chat- and embedding-client composition boundary. Every live call site in the
/// solution goes through here, so which host answers is decided in one place.
/// </summary>
/// <remarks>
/// <para>
/// Two branches, not one per host. An Azure OpenAI resource and a Microsoft Foundry resource speak
/// the Azure OpenAI protocol; Bitdeer, OpenAI, and every other OpenAI-compatible host are an
/// OpenAI client pointed at a different base URL. Adding Together, Groq, or a local vLLM is a new
/// enum value and a row in the resolver's table — nothing changes here.
/// </para>
/// <para>
/// Configuration is captured per composition, never cached process-wide: a cached snapshot would
/// retain a rotated key or bind a later run to an earlier endpoint.
/// </para>
/// </remarks>
internal static class InferenceClientFactory
{
    /// <summary>Builds a chat client for the resolved host and the given model.</summary>
    /// <param name="model">Model or deployment name on the resolved host.</param>
    /// <exception cref="InvalidOperationException">Live configuration is incomplete.</exception>
    internal static IChatClient CreateChatClient(string model) =>
        CreateChatClient(Config.RequireLiveConfiguration(), model);

    /// <summary>Builds a chat client from one immutable configuration snapshot.</summary>
    /// <param name="configuration">The captured live configuration.</param>
    /// <param name="model">Model or deployment name on that host.</param>
    internal static IChatClient CreateChatClient(LiveConfigurationSnapshot configuration, string model)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        return configuration.UsesAzureProtocol
            ? AzureOpenAiClientFactory.Create(configuration).GetChatClient(model.Trim()).AsIChatClient()
            : CreateOpenAiCompatibleClient(configuration).GetChatClient(model.Trim()).AsIChatClient();
    }

    /// <summary>Builds an embedding generator from one immutable configuration snapshot.</summary>
    /// <param name="configuration">The captured live configuration.</param>
    /// <param name="model">Embedding model or deployment name on that host.</param>
    internal static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        LiveConfigurationSnapshot configuration,
        string model)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        return configuration.UsesAzureProtocol
            ? AzureOpenAiClientFactory.Create(configuration).GetEmbeddingClient(model.Trim()).AsIEmbeddingGenerator()
            : CreateOpenAiCompatibleClient(configuration).GetEmbeddingClient(model.Trim()).AsIEmbeddingGenerator();
    }

    private static OpenAIClient CreateOpenAiCompatibleClient(LiveConfigurationSnapshot configuration)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = configuration.Endpoint,
            NetworkTimeout = InferenceProviderEnvironment.NetworkTimeout()
        };

        return new OpenAIClient(
            new ApiKeyCredential(configuration.ApiKey
                ?? throw new InvalidOperationException(
                    $"{configuration.ProviderDisplayName} was resolved without an API key.")),
            options);
    }
}
