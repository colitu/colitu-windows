using System.Globalization;
using System.Windows.Data;

namespace v2rayN.Converters;

/// <summary>
/// Half the element height as a corner radius, for true pill shapes. WPF draws a
/// radius larger than half the size as an ellipse instead of clamping it.
/// </summary>
public sealed class PillRadiusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var height = value is double d && !double.IsNaN(d) ? d : 0;
        return new CornerRadius(Math.Max(0, height / 2));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
