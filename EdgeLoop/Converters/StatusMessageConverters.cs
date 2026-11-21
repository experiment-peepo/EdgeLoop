using System;
using System.Globalization;
using System.Windows.Media;
using EdgeLoop.Classes;
using EdgeLoop.ViewModels;

namespace EdgeLoop.Converters
{
    /// <summary>The status banner's background tint for each message type.</summary>
    public class StatusMessageTypeToBrushConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            (value as StatusMessageType?) switch
            {
                StatusMessageType.Success => ThemeBrushes.Get("SuccessBannerBrush", Color.FromArgb(0x33, 0x90, 0xEE, 0x90)),
                StatusMessageType.Warning => ThemeBrushes.Get("WarningBannerBrush", Color.FromArgb(0x33, 0xFF, 0xD7, 0x00)),
                StatusMessageType.Error => ThemeBrushes.Get("ErrorBannerBrush", Color.FromArgb(0x40, 0xFF, 0x44, 0x44)),
                _ => ThemeBrushes.Get("InfoBannerBrush", Color.FromArgb(0x26, 0xFF, 0xB6, 0xD9))
            };
    }

    /// <summary>The status banner's text colour for each message type.</summary>
    public class StatusMessageTypeToForegroundConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            (value as StatusMessageType?) switch
            {
                StatusMessageType.Success => ThemeBrushes.Get("SuccessTextBrush", Color.FromArgb(0xCC, 0x90, 0xEE, 0x90)),
                StatusMessageType.Warning => ThemeBrushes.Get("WarningTextBrush", Color.FromArgb(0xCC, 0xFF, 0xD7, 0x00)),
                StatusMessageType.Error => ThemeBrushes.Get("ErrorTextBrush", Color.FromRgb(0xFF, 0x7A, 0x7A)),
                _ => ThemeBrushes.Get("InfoTextBrush", Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF))
            };
    }
}
