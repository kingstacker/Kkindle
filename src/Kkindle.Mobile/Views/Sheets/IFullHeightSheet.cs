namespace Kkindle.Mobile.Views.Sheets;

/// <summary>A sheet whose content scrolls on its own, so it is given the full height up front.</summary>
public interface IFullHeightSheet
{
    bool FillsHeight { get; }
}
