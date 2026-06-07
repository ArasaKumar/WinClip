namespace Clippet.Services;

/// <summary>Shared one-shot flag so the app ignores the WM_CLIPBOARDUPDATE caused by its own
/// paste-back. Set before SetClipboardData; cleared when the resulting update arrives.</summary>
public sealed class ClipboardGate
{
    private volatile bool _suppress;

    public void Arm() => _suppress = true;

    /// <summary>Returns true (and disarms) if the next update should be ignored.</summary>
    public bool ConsumeIfArmed()
    {
        if (!_suppress) return false;
        _suppress = false;
        return true;
    }
}
