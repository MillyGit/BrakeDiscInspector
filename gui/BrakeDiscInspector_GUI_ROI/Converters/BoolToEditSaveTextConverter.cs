using System;
using System.Globalization;
using System.Windows.Data;

namespace BrakeDiscInspector_GUI_ROI.Converters
{
    public class BoolToEditSaveTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b) ? "Editar ROI" : "Save ROI";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
