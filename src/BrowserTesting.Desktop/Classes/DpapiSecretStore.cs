#pragma warning disable CA1416
using System.Security.Cryptography;
using System.Text;

namespace BrowserTesting.Desktop.Classes;

public sealed class DpapiSecretStore(SqliteChatRepository repository)
{
    public async Task SaveSecretAsync(Guid chatId, string name, string value, CancellationToken cancellationToken)
    {
        var clearBytes = Encoding.UTF8.GetBytes(value);
        var encrypted = ProtectedData.Protect(clearBytes, null, DataProtectionScope.CurrentUser);
        await repository.SaveSecretAsync(chatId, name, Convert.ToBase64String(encrypted), cancellationToken);
    }

    public async Task<string?> GetSecretAsync(Guid chatId, string name, CancellationToken cancellationToken)
    {
        var encryptedValue = await repository.GetSecretAsync(chatId, name, cancellationToken);
        if (string.IsNullOrWhiteSpace(encryptedValue))
        {
            return null;
        }

        var encryptedBytes = Convert.FromBase64String(encryptedValue);
        var clearBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(clearBytes);
    }

    public async Task<IReadOnlyList<string>> ListSecretNamesAsync(Guid chatId, CancellationToken cancellationToken) =>
        await repository.ListSecretNamesAsync(chatId, cancellationToken);
}
