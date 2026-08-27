using System.Globalization;
using System.Windows.Data;

namespace AIWorkStation;

public sealed class BooleanToOnOffConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "开" : "关";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
