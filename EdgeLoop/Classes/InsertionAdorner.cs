using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace EdgeLoop.Classes {
    public class InsertionAdorner : Adorner {
        private readonly bool _isAfter;
        private readonly AdornerLayer _adornerLayer;
        private readonly Pen _pen;
        
        public InsertionAdorner(UIElement adornedElement, bool isAfter, AdornerLayer adornerLayer) 
            : base(adornedElement) {
            _isAfter = isAfter;
            _adornerLayer = adornerLayer;
            
            // Pink accent color matching app theme
            _pen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x69, 0xB4)), 3);
            _pen.Freeze();
            
            IsHitTestVisible = false;
            _adornerLayer.Add(this);
        }
        
        protected override void OnRender(DrawingContext drawingContext) {
            var adornedRect = new Rect(AdornedElement.RenderSize);
            
            // Draw horizontal line at top or bottom of item
            double y = _isAfter ? adornedRect.Bottom : adornedRect.Top;
            var startPoint = new Point(adornedRect.Left + 10, y);
            var endPoint = new Point(adornedRect.Right - 10, y);
            
            drawingContext.DrawLine(_pen, startPoint, endPoint);
            
            // Draw small triangles at ends for polish
            DrawTriangle(drawingContext, startPoint, true);
            DrawTriangle(drawingContext, endPoint, false);
        }
        
        private void DrawTriangle(DrawingContext dc, Point point, bool isLeft) {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open()) {
                double direction = isLeft ? 1 : -1;
                ctx.BeginFigure(point, true, true);
                ctx.LineTo(new Point(point.X + (6 * direction), point.Y - 4), true, false);
                ctx.LineTo(new Point(point.X + (6 * direction), point.Y + 4), true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(_pen.Brush, null, geometry);
        }
        
        public void Detach() {
            _adornerLayer.Remove(this);
        }
    }
}

