using DuplicateFileCleanerPro.App.Results;
using Microsoft.UI.Xaml.Data;

namespace DuplicateFileCleanerPro.App.Results;

public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is long bytes ? ResultDisplayFormatter.FormatBytes(bytes) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class DateTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is DateTimeOffset timestamp ? ResultDisplayFormatter.FormatDateTime(timestamp) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
