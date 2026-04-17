using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace BrowserTesting.Desktop.Services;

public interface ITextFileSaveService
{
    Task<string?> SaveTextAsync(string title, string suggestedFileName, string content, CancellationToken cancellationToken);
}

public sealed class TextFileSaveService(Window owner) : ITextFileSaveService
{
    public async Task<string?> SaveTextAsync(string title, string suggestedFileName, string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (!owner.StorageProvider.CanSave)
        {
            throw new InvalidOperationException("File export is not available on this platform.");
        }

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "txt",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("Text files")
                {
                    Patterns = ["*.txt"],
                    MimeTypes = ["text/plain"],
                },
                new FilePickerFileType("All files")
                {
                    Patterns = ["*.*"],
                },
            ],
        });

        if (file is null)
        {
            return null;
        }

        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        stream.Position = 0;
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content);
        await writer.FlushAsync(cancellationToken);
        return file.TryGetLocalPath() ?? file.Name;
    }
}
