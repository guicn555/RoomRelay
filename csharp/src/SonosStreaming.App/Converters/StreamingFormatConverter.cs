using Microsoft.UI.Xaml.Data;
using SonosStreaming.Core.Audio;

namespace SonosStreaming.App.Converters;

public sealed class StreamingFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is StreamingFormat fmt ? fmt.DisplayName() : value?.ToString() ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
