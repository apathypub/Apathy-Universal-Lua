namespace Apathy.Models;

/// <summary>A runnable script file shown in the Scripts tab.</summary>
public sealed class ScriptEntry : ObservableObject
{
    public ScriptEntry(string name, string path, bool isInternal = false)
    {
        Name = name;
        Path = path;
        IsInternal = isInternal;
    }

    public string Name { get; }
    public string Path { get; }

    /// <summary>True for .csi scripts in the internal/ subfolder; they have full app access.</summary>
    public bool IsInternal { get; }

    public bool IsLoaded { get; set => SetField(ref field, value); }
}
