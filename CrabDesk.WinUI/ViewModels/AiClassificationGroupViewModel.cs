using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CrabDesk.WinUI.ViewModels;

/// <summary>
/// Represents a grouped category card in the AI activity inspector tab.
/// </summary>
public sealed class AiClassificationGroupViewModel : ObservableObject
{
    public required string CategoryName { get; init; }
    public bool IsUncertainGroup { get; init; }
    public ObservableCollection<AiWorkbenchItemViewModel> Items { get; } = [];
    public int ItemCount => Items.Count;
}
