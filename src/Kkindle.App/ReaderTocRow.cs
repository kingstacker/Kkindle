using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Kkindle.Infrastructure;

namespace Kkindle;

/// <summary>
/// One rendered row of the expanded TOC rail. The reader keeps the book's
/// navigation as a flat, ordered list (progress slider, previous/next chapter
/// and search all index into it); this wrapper adds the fold state and the
/// indentation that turn that list back into the book's own tree.
/// </summary>
public sealed class ReaderTocRow : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isCurrent;

    public ReaderTocRow(EpubReaderNavigationItem item, bool hasChildren, bool isExpanded)
    {
        Item = item;
        HasChildren = hasChildren;
        _isExpanded = isExpanded;
    }

    public EpubReaderNavigationItem Item { get; }
    public bool HasChildren { get; }
    public string Title => Item.Title;
    public int Level => Item.Level;

    // The 3 px selection bar sits in its own column, so the indent belongs to
    // the row content. 14 px per level keeps four levels inside the 286 px
    // rail without squeezing the title column.
    public Thickness Indent => new(Item.Level * 14d, 0, 0, 0);

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToggleRotation));
        }
    }

    // Keep the current-row marker in the data item rather than reading
    // ListBoxItem.IsSelected. The latter is a recycled container state and can
    // briefly describe more than one visual row while a folded source changes.
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            OnPropertyChanged();
        }
    }

    // The vector arrow points right when collapsed and rotates clockwise to
    // point down when the branch is open. A numeric property keeps the
    // rotation animatable without changing the row's layout or font size.
    public double ToggleRotation => IsExpanded ? 90d : 0d;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
