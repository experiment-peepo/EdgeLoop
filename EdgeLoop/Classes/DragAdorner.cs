using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// A see-through picture of a playlist card that follows the mouse while the card is dragged.
    /// It's a snapshot taken when the drag starts, so it stays right even if the list scrolls and
    /// reuses the card's container for another video.
    /// </summary>
    public class DragAdorner : Adorner
    {
        private readonly Image _child;
        private double _leftOffset;
        private double _topOffset;

        /// <summary>The ghost's fixed left position within the adorned element.</summary>
        public double HorizontalOffset { get; set; }

        public DragAdorner(UIElement adornedElement, FrameworkElement draggedElement, double opacity = 0.7)
            : base(adornedElement)
        {
            _child = new Image
            {
                Source = Snapshot(draggedElement),
                Width = draggedElement.ActualWidth,
                Height = draggedElement.ActualHeight,
                Opacity = opacity,
                IsHitTestVisible = false
            };
            AddVisualChild(_child);
            IsHitTestVisible = false;
        }

        private static ImageSource Snapshot(FrameworkElement element)
        {
            var dpi = VisualTreeHelper.GetDpi(element);
            int width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX));
            int height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY));

            // Draw through a VisualBrush so the element's position in its parent doesn't offset the picture
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
                context.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));

            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        public void SetPosition(double left, double top)
        {
            _leftOffset = left;
            _topOffset = top;
            (Parent as AdornerLayer)?.Update(AdornedElement);
        }

        protected override Size MeasureOverride(Size constraint)
        {
            _child.Measure(constraint);
            return _child.DesiredSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _child.Arrange(new Rect(_child.DesiredSize));
            return finalSize;
        }

        public override GeneralTransform GetDesiredTransform(GeneralTransform transform)
        {
            var result = new GeneralTransformGroup();
            result.Children.Add(new TranslateTransform(_leftOffset, _topOffset));
            var baseTransform = base.GetDesiredTransform(transform);
            if (baseTransform != null)
                result.Children.Add(baseTransform);
            return result;
        }

        protected override Visual GetVisualChild(int index) => _child;
        protected override int VisualChildrenCount => 1;
    }
}
