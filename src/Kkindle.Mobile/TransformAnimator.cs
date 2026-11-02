using Avalonia;
using Avalonia.Animation;
using Avalonia.Media;

namespace Kkindle.Mobile;

/// <summary>
/// Avalonia 12 ships <c>TransformOperationsAnimator</c> internally but does not
/// register it, so a keyframe that sets <c>RenderTransform</c> fails with
/// "No animator registered for the property". This animator fills that gap by
/// interpolating the two transforms' matrices; the Liquid Glass backdrop only
/// uses translation and scale, for which matrix lerping is exact enough.
/// </summary>
internal sealed class TransformAnimator : InterpolatingAnimator<ITransform>
{
    public override ITransform Interpolate(double progress, ITransform oldValue, ITransform newValue)
    {
        var from = oldValue.Value;
        var to = newValue.Value;
        var value = new Matrix(
            Lerp(from.M11, to.M11, progress),
            Lerp(from.M12, to.M12, progress),
            Lerp(from.M21, to.M21, progress),
            Lerp(from.M22, to.M22, progress),
            Lerp(from.M31, to.M31, progress),
            Lerp(from.M32, to.M32, progress));
        return new MatrixTransform(value);
    }

    private static double Lerp(double from, double to, double progress) =>
        from + ((to - from) * progress);
}
