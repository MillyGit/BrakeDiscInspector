using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using Size = System.Windows.Size;

namespace BrakeDiscInspector_GUI_ROI.Models
{
    public class ResizeAdorner : Adorner
    {
        private readonly VisualCollection _visuals;

        private readonly Thumb _topLeft;
        private readonly Thumb _topRight;
        private readonly Thumb _bottomRight;
        private readonly Thumb _bottomLeft;

        public event Action? OnResizeEnd;

        public ResizeAdorner(UIElement adornedElement) : base(adornedElement)
        {
            _visuals = new VisualCollection(this);

            _topLeft = CreateThumb();
            _topRight = CreateThumb();
            _bottomRight = CreateThumb();
            _bottomLeft = CreateThumb();

            _topLeft.Cursor = Cursors.SizeNWSE;
            _bottomRight.Cursor = Cursors.SizeNWSE;
            _topRight.Cursor = Cursors.SizeNESW;
            _bottomLeft.Cursor = Cursors.SizeNESW;

            _topLeft.DragDelta += (s, e) => ResizeFromCorner(Corner.TopLeft, e);
            _topRight.DragDelta += (s, e) => ResizeFromCorner(Corner.TopRight, e);
            _bottomRight.DragDelta += (s, e) => ResizeFromCorner(Corner.BottomRight, e);
            _bottomLeft.DragDelta += (s, e) => ResizeFromCorner(Corner.BottomLeft, e);

            _topLeft.DragCompleted += (s, e) => OnResizeEnd?.Invoke();
            _topRight.DragCompleted += (s, e) => OnResizeEnd?.Invoke();
            _bottomRight.DragCompleted += (s, e) => OnResizeEnd?.Invoke();
            _bottomLeft.DragCompleted += (s, e) => OnResizeEnd?.Invoke();

            _visuals.Add(_topLeft);
            _visuals.Add(_topRight);
            _visuals.Add(_bottomRight);
            _visuals.Add(_bottomLeft);

            IsHitTestVisible = true;
        }

        private Thumb CreateThumb()
        {
            return new Thumb
            {
                Width = 12,
                Height = 12,
                Background = Brushes.White,
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1)
            };
        }

        protected override int VisualChildrenCount => _visuals.Count;

        protected override Visual GetVisualChild(int index) => _visuals[index];

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (AdornedElement is not FrameworkElement fe) return finalSize;

            double w = fe.ActualWidth;
            double h = fe.ActualHeight;

            const double t = 12.0;
            double half = t / 2.0;

            // Coloca las 4 esquinas formando un cuadrado perfecto alrededor del elemento
            _topLeft.Arrange(new Rect(-half, -half, t, t));
            _topRight.Arrange(new Rect(w - half, -half, t, t));
            _bottomRight.Arrange(new Rect(w - half, h - half, t, t));
            _bottomLeft.Arrange(new Rect(-half, h - half, t, t));

            return finalSize;
        }

        private enum Corner { TopLeft, TopRight, BottomRight, BottomLeft }

        private void ResizeFromCorner(Corner c, DragDeltaEventArgs e)
        {
            if (AdornedElement is not FrameworkElement fe) return;

            // Posición y tamaño actuales en Canvas
            double left = Canvas.GetLeft(fe);
            double top = Canvas.GetTop(fe);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;

            double width = fe.ActualWidth;
            double height = fe.ActualHeight;

            const double MIN = 10.0;

            // Puntos fijos por esquina (mantener la esquina opuesta)
            double tlx = left, tly = top;
            double trx = left + width, tryy = top;
            double brx = left + width, bry = top + height;
            double blx = left, bly = top + height;

            // Queremos mantener **cuadrado** para círculos/annulus (Ellipse) y cuadrados;
            // tomaremos un único "size" y ajustaremos en base a la esquina opuesta.
            double size;

            switch (c)
            {
                case Corner.TopLeft:
                    {
                        // esquina opuesta fija = BottomRight (brx,bry)
                        size = Math.Max(MIN, Math.Max(brx - (left + e.HorizontalChange), bry - (top + e.VerticalChange)));
                        double newLeft = brx - size;
                        double newTop = bry - size;
                        Canvas.SetLeft(fe, newLeft);
                        Canvas.SetTop(fe, newTop);
                        fe.Width = size;
                        fe.Height = size;
                        break;
                    }
                case Corner.TopRight:
                    {
                        // opuesta fija = BottomLeft (blx,bly)
                        size = Math.Max(MIN, Math.Max((left + width + e.HorizontalChange) - blx, bly - (top + e.VerticalChange)));
                        double newLeft = blx;
                        double newTop = bly - size;
                        Canvas.SetLeft(fe, newLeft);
                        Canvas.SetTop(fe, newTop);
                        fe.Width = size;
                        fe.Height = size;
                        break;
                    }
                case Corner.BottomRight:
                    {
                        // opuesta fija = TopLeft (tlx,tly)
                        size = Math.Max(MIN, Math.Max((left + width + e.HorizontalChange) - tlx, (top + height + e.VerticalChange) - tly));
                        double newLeft = tlx;
                        double newTop = tly;
                        Canvas.SetLeft(fe, newLeft);
                        Canvas.SetTop(fe, newTop);
                        fe.Width = size;
                        fe.Height = size;
                        break;
                    }
                case Corner.BottomLeft:
                    {
                        // opuesta fija = TopRight (trx,tryy)
                        size = Math.Max(MIN, Math.Max(trx - (left + e.HorizontalChange), (top + height + e.VerticalChange) - tryy));
                        double newLeft = trx - size;
                        double newTop = tryy;
                        Canvas.SetLeft(fe, newLeft);
                        Canvas.SetTop(fe, newTop);
                        fe.Width = size;
                        fe.Height = size;
                        break;
                    }
            }

            InvalidateArrange();
        }
    }
}
