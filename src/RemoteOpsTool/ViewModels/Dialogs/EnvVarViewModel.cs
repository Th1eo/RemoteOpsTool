using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels.Dialogs;

public partial class EnvVarViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly IEnvVarService _envVarService;
    private readonly ILogService _logService;
    private readonly IPsExecService _psExecService;

    [ObservableProperty] private string _selectedTarget = "系统环境变量";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private List<string> _sessionUsers = [];
    [ObservableProperty] private EnvVarRow? _selectedVariable;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _canOperate = true;
    [ObservableProperty] private bool _canDeleteSelected;
    [ObservableProperty] private bool _canEditSelected;
    public EnvVarRow? RightClickedRow { get; set; }

    public ObservableCollection<EnvVarRow> Variables { get; } = [];
    public ObservableCollection<EnvVarRow> FilteredVariables { get; } = [];
    public ObservableCollection<string> Targets { get; } = ["系统环境变量"];

    public EnvVarViewModel(MainViewModel main, IEnvVarService envVarService, ILogService logService, IPsExecService psExecService)
    {
        _main = main; _envVarService = envVarService; _logService = logService; _psExecService = psExecService;
        _ = ScanSessionUsersAndLoadAsync();
    }

    private async Task ScanSessionUsersAndLoadAsync()
    {
        await ScanSessionUsersAsync();
        await LoadVariablesAsync();
    }

    private string ResolveTarget()
    {
        if (SelectedTarget == "系统环境变量") return "Machine";
        if (SelectedTarget.StartsWith("会话: "))
            return SelectedTarget["会话: ".Length..];
        return "Machine";
    }

    partial void OnSelectedTargetChanged(string value)
    {
        _ = LoadVariablesAsync();
    }

    private async Task LoadVariablesAsync()
    {
        IsLoading = true;
        try
        {
            var host = _main.GetTargetHost();
            var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
            if (cred == null) { IsLoading = false; return; }
            var password = _main.Connection.CredentialService.DecryptPassword(cred);
            var target = ResolveTarget();
            var list = await _envVarService.GetVariablesAsync(host, cred.UserName, password ?? string.Empty, target);
            foreach (var r in Variables) r.PropertyChanged -= OnEnvVarRowPropertyChanged;
            Variables.Clear();
            var displayTarget = SelectedTarget.StartsWith("会话: ") 
                ? $"用户: {SelectedTarget["会话: ".Length..].Split('\\')[^1]}" 
                : "系统";
            foreach (var v in list)
            {
                v.Target = displayTarget;
                var row = new EnvVarRow { Variable = v };
                row.PropertyChanged += OnEnvVarRowPropertyChanged;
                Variables.Add(row);
            }
            ApplyFilter();
        }
        finally { IsLoading = false; }
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        FilteredVariables.Clear();
        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? Variables
            : Variables.Where(v => v.Variable.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                                 || v.Variable.Value.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        foreach (var v in filtered) FilteredVariables.Add(v);
    }

    private void OnEnvVarRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EnvVarRow.IsChecked))
        {
            var count = Variables.Count(v => v.IsChecked);
            CanDeleteSelected = count > 0;
            CanEditSelected = count == 1;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await ScanSessionUsersAsync();
        await LoadVariablesAsync();
    }

    [RelayCommand]
    private async Task ScanSessionUsersAsync()
    {
        try
        {
            var toRemove = Targets.Where(t => t.StartsWith("会话: ")).ToList();
            foreach (var r in toRemove) Targets.Remove(r);
            SessionUsers.Clear();

            var host = _main.GetTargetHost();
            var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
            if (cred == null) return;
            var password = _main.Connection.CredentialService.DecryptPassword(cred);

            var users = await _envVarService.GetLoggedOnUsersAsync(host, cred.UserName, password ?? string.Empty);
            foreach (var user in users.Where(u => !string.IsNullOrWhiteSpace(u)))
            {
                var displayName = $"会话: {user}";
                if (!Targets.Contains(displayName))
                {
                    Targets.Add(displayName);
                    SessionUsers.Add(user);
                }
            }
        }
        catch { }
    }

    [RelayCommand]
    private async Task AddVariableAsync()
    {
        var dialog = new Views.Dialogs.EnvVarEditDialog("", "")
        {
            Owner = System.Windows.Application.Current.MainWindow,
            Title = "新建环境变量"
        };
        dialog.ShowDialog();
        if (!dialog.Confirmed || string.IsNullOrWhiteSpace(dialog.VariableName)) return;

        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);

        await _envVarService.SetVariableAsync(host, cred.UserName, password ?? string.Empty,
            dialog.VariableName, dialog.VariableValue, ResolveTarget());
        await LoadVariablesAsync();
    }

    [RelayCommand]
    private async Task EditSelectedAsync()
    {
        var row = RightClickedRow ?? Variables.FirstOrDefault(v => v.IsChecked);
        RightClickedRow = null;
        if (row == null) return;

        var dialog = new Views.Dialogs.EnvVarEditDialog(row.Variable.Name, row.Variable.Value)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        dialog.ShowDialog();
        if (!dialog.Confirmed) return;

        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);

        // If variable name changed, delete the old one first
        if (!string.Equals(row.Variable.Name, dialog.VariableName, StringComparison.OrdinalIgnoreCase))
            await _envVarService.DeleteVariableAsync(host, cred.UserName, password ?? string.Empty, row.Variable.Name, ResolveTarget());

        await _envVarService.SetVariableAsync(host, cred.UserName, password ?? string.Empty,
            dialog.VariableName, dialog.VariableValue, ResolveTarget());
        await LoadVariablesAsync();
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var row = RightClickedRow;
        RightClickedRow = null;

        var items = row != null
            ? [row]
            : Variables.Where(v => v.IsChecked).ToList();

        if (items.Count == 0) return;

        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);

        foreach (var item in items)
            await _envVarService.DeleteVariableAsync(host, cred.UserName, password ?? string.Empty, item.Variable.Name, ResolveTarget());
        await LoadVariablesAsync();
    }
}

public partial class EnvVarRow : ObservableObject
{
    public EnvVariableInfo Variable { get; set; } = null!;
    [ObservableProperty] private bool _isChecked;
}
