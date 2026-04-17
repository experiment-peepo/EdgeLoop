using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace EdgeLoop.Classes {
    public class DragAdorner : Adorner {
        private readonly UIElement _child;
        private double _leftOffset;
        private double _topOffset;
        
        public DragAdorner(UIElement adornedElement, UIElement draggedElement, double opacity = 0.7) 
            : base(adornedElement) {
            var brush = new VisualBrush(draggedElement) { Opacity = opacity };
            
            var bounds = VisualTreeHelper.GetDescendantBounds(draggedElement);
            var rectangle = new System.Windows.Shapes.Rectangle {
                Width = bounds.Width,
                Height = bounds.Height,
                Fill = brush,
                IsHitTestVisible = false
            };
            
            _child = rectangle;
            IsHitTestVisible = false;
        }
        
        public void SetPosition(double left, double top) {
            _leftOffset = left;
            _topOffset = top;
            
            var layer = Parent as AdornerLayer;
            layer?.Update(AdornedElement);
        }
        
        protected override Size MeasureOverride(Size constraint) {
            _child.Measure(constraint);
            return _child.DesiredSize;
        }
        
        protected override Size ArrangeOverride(Size finalSize) {
            _child.Arrange(new Rect(_child.DesiredSize));
            return finalSize;
        }
        
        public override GeneralTransform GetDesiredTransform(GeneralTransform transform) {
            var result = new GeneralTransformGroup();
            result.Children.Add(new TranslateTransform(_leftOffset, _topOffset));
            var baseTransform = base.GetDesiredTransform(transform);
            if (baseTransform != null) {
                result.Children.Add(baseTransform);
            }
            return result;
        }
        
        protected override Visual GetVisualChild(int index) => _child;
        protected override int VisualChildrenCount => 1;
    }
}

