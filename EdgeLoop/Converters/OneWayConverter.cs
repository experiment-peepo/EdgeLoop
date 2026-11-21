using System;
using System.Globalization;
using System.Windows.Data;

namespace EdgeLoop.Converters
{
    /// <summary>Base for converters that only convert from the source to the view.</summary>
    public abstract class OneWayConverter : IValueConverter
    {
        public abstract object Convert(object value, Type targetType, object parameter, CultureInfo culture);

        public virtual object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException($"{GetType().Name} only converts one way.");
    }
}
