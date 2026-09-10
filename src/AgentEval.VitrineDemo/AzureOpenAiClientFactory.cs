// SPDX-License-Identifier: MIT
// Copyright (c) 2026 José Luis Latorre Millas

using Azure.AI.OpenAI;
using Azure.Identity;
using System.Collections.Concurrent;

namespace Galaxus.RecommendationAgent;

/// <summary>
/// The single Azure OpenAI resource-client composition boundary used by chat, workflow, judge,
/// and embedding adapters. Credential selection happens here and nowhere else.
/// </summary>
internal static class AzureOpenAiClientFactory
{
    // Azure credential instances cache tokens and are thread-safe. Reusing these avoids rebuilding
    // the DefaultAzureCredential chain or managed-identity token cache for every model client.
    private static readonly Lazy<DefaultAzureCredential> LocalDevelopmentCredential =
        new(static () => new DefaultAzureCredential(), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<ManagedIdentityCredential> SystemAssignedCredential =
        new(
            static () => new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly ConcurrentDictionary<string, ManagedIdentityCredential> UserAssignedCredentials =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a resource client and returns the exact configuration snapshot it uses.</summary>
    internal static AzureOpenAIClient CreateConfigured(out LiveConfigurationSnapshot configuration)
    {
        configuration = Config.RequireLiveConfiguration();
        return Create(configuration);
    }

    /// <summary>Creates a resource client from one immutable configuration snapshot.</summary>
    internal static AzureOpenAIClient Create(LiveConfigurationSnapshot configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // The resource client is composition-scoped on purpose. Config reflects environment
        // changes between runs, including API-key rotation; a process-wide client cache would
        // retain an old key or bind a later run to an earlier endpoint. Identity credential
        // instances are reused above because their token caches are designed for that lifetime.

        return configuration.AuthenticationMode switch
        {
            AzureOpenAiAuthenticationMode.ApiKey => new AzureOpenAIClient(
                configuration.Endpoint,
                configuration.ApiKeyCredential
                    ?? throw new InvalidOperationException("API-key authentication was selected without a credential.")),

            // This explicit mode is intended only for local development. Hosted production uses
            // ManagedIdentityCredential below so it has no ambiguous fallback chain.
            AzureOpenAiAuthenticationMode.DefaultAzureCredential => new AzureOpenAIClient(
                configuration.Endpoint,
                LocalDevelopmentCredential.Value),

            AzureOpenAiAuthenticationMode.ManagedIdentity => new AzureOpenAIClient(
                configuration.Endpoint,
                configuration.ManagedIdentityClientId is { } clientId
                    ? UserAssignedCredentials.GetOrAdd(
                        clientId,
                        static id => new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(id)))
                    : SystemAssignedCredential.Value),

            _ => throw new InvalidOperationException("Unsupported Azure OpenAI authentication mode.")
        };
    }
}
