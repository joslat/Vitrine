// SPDX-License-Identifier: MIT

namespace AgentEval.VitrineDemo.App.Artifacts;

public interface IArtifactSaveService
{
    Task<string?> SaveAsync(string suggestedFileName, string content, string mimeType, CancellationToken cancellationToken = default);
}

public sealed class NullArtifactSaveService : IArtifactSaveService
{
    private NullArtifactSaveService() { }
    public static NullArtifactSaveService Instance { get; } = new();
    public Task<string?> SaveAsync(string suggestedFileName, string content, string mimeType, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
