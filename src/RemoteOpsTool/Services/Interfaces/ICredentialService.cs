using System.Collections.ObjectModel;
using RemoteOpsTool.Models;

namespace RemoteOpsTool.Services.Interfaces;

public interface ICredentialService
{
    ObservableCollection<CredentialInfo> Credentials { get; }

    Task LoadAsync();
    Task SaveAsync();
    void Add(CredentialInfo credential);
    void Remove(CredentialInfo credential);
    void ClearAll();
    string? DecryptPassword(CredentialInfo credential);
    List<CredentialInfo> GetSelectedCredentials();
}
