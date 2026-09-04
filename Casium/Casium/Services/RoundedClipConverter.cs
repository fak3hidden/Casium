using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Casium.Services
{
    /// <summary>Builds a rounded-rectangle clip geometry from an element's ActualWidth/ActualHeight.</summary>
    public sealed class RoundedClipConverter : IMultiValueConverter
    {
        public double Radius { get; set; }

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2 || !(values[0] is double) || !(values[1] is double))
            {
                return null;
            }
            double w = (double)values[0], h = (double)values[1];
            if (w <= 0 || h <= 0)
            {
                return null;
            }
            var g = new RectangleGeometry(new Rect(0, 0, w, h), Radius, Radius);
            g.Freeze();
            return g;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
