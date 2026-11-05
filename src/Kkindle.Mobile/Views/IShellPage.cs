namespace Kkindle.Mobile.Views;

/// <summary>
/// A page that owns work which must stop when it leaves the navigation history.
/// Detaching is not enough: a page pushed on top detaches the one beneath it,
/// which the user can still return to.
/// </summary>
public interface IShellPage
{
    void OnClosed();
}
