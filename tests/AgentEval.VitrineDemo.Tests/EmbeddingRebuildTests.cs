// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Galaxus.RecommendationAgent.Retrieval;

namespace AgentEval.VitrineDemo.Tests;

public sealed class EmbeddingRebuildTests
{
    [Fact]
    public void OutputResolverFindsTheRenamedVitrineProjectDataDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vitrine-output-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "src", "AgentEval.VitrineDemo");
        var binaryDirectory = Path.Combine(projectDirectory, "bin", "Release", "net10.0");

        try
        {
            Directory.CreateDirectory(binaryDirectory);
            File.WriteAllText(
                Path.Combine(projectDirectory, EmbeddingCacheBuilder.ProjectFileName),
                "<Project />");

            var actual = EmbeddingCacheBuilder.ResolveOutputDirectory(binaryDirectory, root);

            Assert.Equal(Path.Combine(projectDirectory, EmbeddingCacheBuilder.DataFolderName), actual);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RebuildCommandWithoutPaidConfirmationFailsBeforeProviderReadiness()
    {
        var result = await RunCliAsync("--rebuild-embeddings");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("not confirmed", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No provider call was made", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Embedding 99 product documents", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LiveSubjectWithoutPaidConfirmationFailsBeforeProviderReadiness()
    {
        var result = await RunCliAsync("1", "--live");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("not confirmed", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No provider call was made", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Customer :", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealVectorQueriesWithoutPaidConfirmationFailBeforeRetrieval()
    {
        var result = await RunCliAsync("1", "--real-vectors");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("not confirmed", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No provider call was made", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Customer :", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmedEmbeddingRebuildStillFailsReadinessWithoutCredentials()
    {
        var result = await RunCliAsync("--rebuild-embeddings", "--confirm-paid");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Cannot rebuild the embedding assets", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Embedding 99 product documents", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HeadlineSelectorIsOfflineUnlessLiveIsExplicitlyConfirmed()
    {
        var result = await RunCliAsync("1");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("no model call was made", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("deployment:", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CredentialsAloneCannotChangeHeadlineSelectorFromOffline()
    {
        var result = await RunConfiguredLookingCliAsync("1");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("no model call was made", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("sentinel-vitrine-cli.example.invalid", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("synthetic-test-key", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InteractiveEmbeddingRebuildRequiresPaidConfirmation()
    {
        var result = await RunInteractiveCliAsync("R");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Embedding rebuild was not confirmed", result.Output, StringComparison.Ordinal);
        Assert.Contains("No provider call was made", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Embedding 99 product documents", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmedRealVectorMenuDoesNotClaimNoProviderExecution()
    {
        var result = await RunInteractiveCliAsync("Q", "--real-vectors", "--confirm-paid");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Real-vector query embedding is confirmed", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("selectors are offline/no-provider", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmedInteractiveRebuildPropagatesReadinessFailure()
    {
        var result = await RunInteractiveCliAsync("RQ", "--confirm-paid");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Cannot rebuild the embedding assets", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Embedding 99 product documents", result.Output, StringComparison.Ordinal);
    }

    private static Task<(int ExitCode, string Output)> RunCliAsync(params string[] arguments) =>
        RunCliCoreAsync(arguments, standardInput: null, configuredLookingEnvironment: false);

    private static Task<(int ExitCode, string Output)> RunConfiguredLookingCliAsync(params string[] arguments) =>
        RunCliCoreAsync(arguments, standardInput: null, configuredLookingEnvironment: true);

    private static Task<(int ExitCode, string Output)> RunInteractiveCliAsync(
        string input,
        params string[] arguments) => RunCliCoreAsync(arguments, input, configuredLookingEnvironment: false);

    private static async Task<(int ExitCode, string Output)> RunCliCoreAsync(
        IReadOnlyList<string> arguments,
        string? standardInput,
        bool configuredLookingEnvironment)
    {
        var repositoryRoot = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(Path.Combine(
            repositoryRoot,
            "src",
            "AgentEval.VitrineDemo",
            "AgentEval.VitrineDemo.csproj"));
        start.ArgumentList.Add("--configuration");
        start.ArgumentList.Add(configuration);
        start.ArgumentList.Add("--no-build");
        start.ArgumentList.Add("--no-restore");
        start.ArgumentList.Add("--");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove("AZURE_OPENAI_ENDPOINT");
        start.Environment.Remove("AZURE_OPENAI_API_KEY");
        start.Environment.Remove("AZURE_OPENAI_DEPLOYMENT");
        start.Environment.Remove("AZURE_OPENAI_EMBEDDING_DEPLOYMENT");
        if (configuredLookingEnvironment)
        {
            start.Environment["AZURE_OPENAI_ENDPOINT"] = "https://sentinel-vitrine-cli.example.invalid/";
            start.Environment["AZURE_OPENAI_API_KEY"] = "synthetic-test-key";
            start.Environment["AZURE_OPENAI_DEPLOYMENT"] = "synthetic-test-deployment";
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await standardOutput + await standardError);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgentEval.VitrineDemo.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the VITRINE repository root.");
    }
}
