using System;
using System.Globalization;

namespace EdgeLoop.Converters
{
    public class InvertedBooleanConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

        public override object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    }

    /// <summary>"video" for 1, otherwise "videos".</summary>
    public class PluralConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is 1 ? "video" : "videos";
    }

    /// <summary>
    /// Shows an item opacity (0 to 0.9, the overlay's maximum) as a 0-100 percentage; Opaque Mode's 1.0 still shows 100.
    /// </summary>
    public class OpacityToIntConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is double opacity
                ? Math.Min(100, (int)Math.Round(opacity / OpacityScaleConverter.MaxOpacity * 100)).ToString(culture)
                : "100";
    }

    /// <summary>Maps an item opacity (0 to 0.9) to a 0-1 slider and back.</summary>
    public class OpacityScaleConverter : OneWayConverter
    {
        public const double MaxOpacity = 0.90;

        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is double opacity ? opacity / MaxOpacity : 1.0;

        public override object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is double sliderValue ? sliderValue * MaxOpacity : 0.0;
    }

    /// <summary>A progress of 0 or less means "unknown": show the bar as indeterminate.</summary>
    public class IsProgressIndeterminateConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is not double progress || progress <= 0;
    }

    /// <summary>True when the value is below the number given as the parameter, e.g. for narrow-window layouts.</summary>
    public class LessThanConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is double number
            && double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var limit)
            && number < limit;
    }
}
