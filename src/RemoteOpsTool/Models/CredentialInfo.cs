using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteOpsTool.Models;

public partial class CredentialInfo : ObservableObject
{
    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string _encryptedPassword = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    public string DisplayText => string.IsNullOrWhiteSpace(Description)
        ? UserName
        : $"{UserName} [{Description}]";

    public string MaskedDisplay
    {
        get
        {
            var lastBackslash = UserName.LastIndexOf('\\');
            if (lastBackslash >= 0 && lastBackslash < UserName.Length - 1)
                return $"{UserName[..(lastBackslash + 1)]}{UserName[(lastBackslash + 1)..Math.Min(lastBackslash + 4, UserName.Length)]}***";
            return UserName.Length > 3 ? $"{UserName[..3]}***" : UserName;
        }
    }
}
