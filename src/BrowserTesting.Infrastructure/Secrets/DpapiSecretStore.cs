using System.Security.Cryptography;
using System.Text;
using BrowserTesting.Core.Abstractions;
using System.Runtime.Versioning;

namespace BrowserTesting.Infrastructure.Secrets;

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(IChatRepository repository) : ISecretStore
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
