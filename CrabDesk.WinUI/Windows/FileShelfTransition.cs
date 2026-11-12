using CrabDesk.Core;

namespace CrabDesk.WinUI.Windows;

internal sealed class FileShelfTransition
{
    private const double Response = 18;
    private double _velocity;
    private double _target = 1;
    public double Progress { get; private set; } = 1;
    public bool IsRunning => Progress != _target || _velocity != 0;

    public void SetTarget(bool expanded) => _target = expanded ? 1 : 0;

    public void Reset(bool expanded)
    {
        SetTarget(expanded);
        Progress = _target;
        _velocity = 0;
    }

    public void Advance(double seconds)
    {
        if (!IsRunning || seconds <= 0) return;
        // Analytic critical damping keeps the same response at any refresh rate
        // and preserves both position and velocity when the target reverses.
        var displacement = Progress - _target;
        var coefficient = _velocity + Response * displacement;
        var decay = Math.Exp(-Response * seconds);
        Progress = _target + (displacement + coefficient * seconds) * decay;
        _velocity = (_velocity - Response * coefficient * seconds) * decay;
        if (Math.Abs(Progress - _target) < .001 && Math.Abs(_velocity) < .03)
            Reset(_target == 1);
    }

    public static LayoutRect Interpolate(LayoutRect collapsed, LayoutRect expanded, double progress)
    {
        if (progress <= 0) return collapsed;
        if (progress >= 1) return expanded;
        return new(
            collapsed.X + (expanded.X - collapsed.X) * progress,
            collapsed.Y + (expanded.Y - collapsed.Y) * progress,
            collapsed.Width + (expanded.Width - collapsed.Width) * progress,
            collapsed.Height + (expanded.Height - collapsed.Height) * progress);
    }
}
