/*
	Copyright (C) 2026 Llamasoft

	This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

	This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

	You should have received a copy of the GNU General Public License along with this program. If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EdgeLoop.Classes.Censoring;

namespace EdgeLoop.Controls
{
    /// <summary>
    /// Draws censor boxes over the video.
    ///
    /// This sits inside the player window's visual tree, above the video element.
    /// That matters: WPF composites the window's tree first and applies
    /// Window.Opacity to the *result*, so an opaque box here replaces the video
    /// pixels before the window is made translucent. The content is genuinely
    /// gone, not merely faded - which is the whole point, since a translucent
    /// censor over a translucent video would still leak the content.
    /// </summary>
    public sealed class CensorOverlay : FrameworkElement
    {
        public CensorOverlay()
        {
            IsHitTestVisible = false;
        }

        public static readonly DependencyProperty BoxesProperty = DependencyProperty.Register(
            nameof(Boxes), typeof(IReadOnlyList<Detection>), typeof(CensorOverlay),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Boxes normalised (0..1) to the source video frame.</summary>
        public IReadOnlyList<Detection> Boxes
        {
            get => (IReadOnlyList<Detection>)GetValue(BoxesProperty);
            set => SetValue(BoxesProperty, value);
        }

        public static readonly DependencyProperty VideoWidthProperty = DependencyProperty.Register(
            nameof(VideoWidth), typeof(double), typeof(CensorOverlay),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        public double VideoWidth
        {
            get => (double)GetValue(VideoWidthProperty);
            set => SetValue(VideoWidthProperty, value);
        }

        public static readonly DependencyProperty VideoHeightProperty = DependencyProperty.Register(
            nameof(VideoHeight), typeof(double), typeof(CensorOverlay),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        public double VideoHeight
        {
            get => (double)GetValue(VideoHeightProperty);
            set => SetValue(VideoHeightProperty, value);
        }

        public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
            nameof(FillBrush), typeof(Brush), typeof(CensorOverlay),
            new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush FillBrush
        {
            get => (Brush)GetValue(FillBrushProperty);
            set => SetValue(FillBrushProperty, value);
        }

        public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
            nameof(CornerRadius), typeof(double), typeof(CensorOverlay),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        public double CornerRadius
        {
            get => (double)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        public static readonly DependencyProperty ShowLabelsProperty = DependencyProperty.Register(
            nameof(ShowLabels), typeof(bool), typeof(CensorOverlay),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Draws class name and confidence on each box. Diagnostics only.</summary>
        public bool ShowLabels
        {
            get => (bool)GetValue(ShowLabelsProperty);
            set => SetValue(ShowLabelsProperty, value);
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            var boxes = Boxes;
            if (boxes == null || boxes.Count == 0) return;

            double vw = VideoWidth;
            double vh = VideoHeight;
            double cw = ActualWidth;
            double ch = ActualHeight;

            if (vw <= 0 || vh <= 0 || cw <= 0 || ch <= 0) return;

            // Mirror Stretch="Uniform" on the video element: scale to fit, centre,
            // letterbox the remainder. Anything else and the boxes drift off the
            // content on non-matching aspect ratios.
            double scale = Math.Min(cw / vw, ch / vh);
            double dw = vw * scale;
            double dh = vh * scale;
            double ox = (cw - dw) / 2.0;
            double oy = (ch - dh) / 2.0;

            var brush = FillBrush ?? Brushes.Black;
            double radius = CornerRadius;

            foreach (var box in boxes)
            {
                double x = ox + (box.X * dw);
                double y = oy + (box.Y * dh);
                double w = box.Width * dw;
                double h = box.Height * dh;

                if (w <= 0 || h <= 0) continue;

                var rect = new Rect(x, y, w, h);

                if (radius > 0)
                    dc.DrawRoundedRectangle(brush, null, rect, radius, radius);
                else
                    dc.DrawRectangle(brush, null, rect);

                if (!ShowLabels) continue;

                var text = new FormattedText(
                    $"{box.Label} {box.Confidence:0.00}",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    12,
                    Brushes.Lime,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);

                dc.DrawText(text, new Point(x + 2, Math.Max(0, y - 14)));
            }
        }
    }
}
