using System.Collections.ObjectModel;
using RemoteOpsTool.Models;

namespace RemoteOpsTool.Services.Interfaces;

public interface ICredentialService
{
    ObservableCollection<CredentialInfo> Credentials { get; }
    CredentialInfo? SelectedCredential { get; }

    event EventHandler? SelectedCredentialChanged;
    event Action<string>? SaveFailed;

    Task LoadAsync();
    Task SaveAsync();
    void Add(CredentialInfo credential);
    void Remove(CredentialInfo credential);
    void ClearAll();
    void SetSelectedCredential(CredentialInfo? credential);
    string? DecryptPassword(CredentialInfo credential);
    bool TryDecryptPassword(CredentialInfo credential, out string password);
    List<CredentialInfo> GetSelectedCredentials();
}
