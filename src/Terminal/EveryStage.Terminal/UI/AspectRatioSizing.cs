namespace EveryStage.Terminal.UI;

public static class AspectRatioSizing
{
    // Choose integral multiples so repeated DPI/resize events cannot accumulate rounding drift.
    public static Size Fit(Size available, Size ratio)
    {
        if (ratio.Width <= 0 || ratio.Height <= 0) throw new ArgumentOutOfRangeException(nameof(ratio));
        int units = Math.Max(1, Math.Min(available.Width / ratio.Width, available.Height / ratio.Height));
        return new Size(units * ratio.Width, units * ratio.Height);
    }

    public static Size Resize(Size proposed, Size minimum, Size maximum, Size ratio, bool heightDriven)
    {
        Size limit = Fit(maximum, ratio);
        int maxUnits = limit.Width / ratio.Width;
        int minUnits = Math.Min(maxUnits, Math.Max(
            (minimum.Width + ratio.Width - 1) / ratio.Width,
            (minimum.Height + ratio.Height - 1) / ratio.Height));
        int desired = heightDriven ? proposed.Height / ratio.Height : proposed.Width / ratio.Width;
        int units = Math.Clamp(desired, Math.Max(1, minUnits), maxUnits);
        return new Size(units * ratio.Width, units * ratio.Height);
    }
}
