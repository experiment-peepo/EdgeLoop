using System;
using System.Globalization;
using System.Windows;

namespace EdgeLoop.Converters
{
    /// <summary>Visible when the string has text.</summary>
    public class StringToVisibilityConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is string text && !string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Visible when true.</summary>
    public class BooleanToVisibilityConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? Visibility.Visible : Visibility.Collapsed;

        public override object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is Visibility.Visible;
    }

    /// <summary>Visible when false.</summary>
    public class InvertedBooleanToVisibilityConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Visible when the value's name equals the parameter, e.g. ConverterParameter=Local.</summary>
    public class EnumToVisibilityConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null) return Visibility.Collapsed;
            return value.ToString().Equals(parameter.ToString(), StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
