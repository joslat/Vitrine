// SPDX-License-Identifier: MIT

using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AgentEval.VitrineDemo.App.Artifacts;

/// <summary>Platform save-picker adapter; file-picker delegation is the only UI concern here.</summary>
public sealed class AvaloniaArtifactSaveService(Window owner) : IArtifactSaveService
{
    private readonly Window _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public async Task<string?> SaveAsync(
        string suggestedFileName,
        string content,
        string mimeType,
        CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(suggestedFileName);
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export sanitized VITRINE evidence",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices =
            [
                new FilePickerFileType(mimeType == "application/json" ? "JSON evidence" : "Self-contained HTML report")
                {
                    Patterns = [$"*{extension}"],
                    MimeTypes = [mimeType],
                },
            ],
        });
        if (file is null) return null;

        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        return file.Name;
    }
}
