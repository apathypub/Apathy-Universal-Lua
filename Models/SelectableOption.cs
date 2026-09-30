namespace Apathy.Models;

/// <summary>One entry of a multi-select dropdown.</summary>
public sealed class SelectableOption : ObservableObject
{
    public SelectableOption() { }

    public SelectableOption(string name, bool isSelected = false)
    {
        Name = name;
        IsSelected = isSelected;
    }

    public string Name { get; set => SetField(ref field, value); } = "";

    public bool IsSelected { get; set => SetField(ref field, value); }
}
