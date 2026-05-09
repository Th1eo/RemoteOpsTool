using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteOpsTool.Models;

public partial class RegistryTreeNode : ObservableObject
{
    public static readonly RegistryTreeNode Placeholder = new() { Name = "加载中..." };
    public static readonly RegistryTreeNode TimeoutNode = new() { Name = "(加载超时，点击重试)" };
    public static readonly RegistryTreeNode ErrorNode = new() { Name = "(加载失败)" };

    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";

    [ObservableProperty] private System.Collections.Generic.IReadOnlyList<RegistryTreeNode>? _children;
}
