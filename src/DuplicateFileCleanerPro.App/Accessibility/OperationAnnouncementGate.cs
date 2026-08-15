namespace DuplicateFileCleanerPro.App.Accessibility;

/// <summary>Keeps assistive notifications meaningful while the engine continues reporting exact progress.</summary>
public sealed class OperationAnnouncementGate<T> where T : class
{
    private T? lastValue;

    public bool ShouldAnnounce(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (EqualityComparer<T?>.Default.Equals(lastValue, value)) return false;
        lastValue = value;
        return true;
    }

    public void Reset() => lastValue = default;
}
