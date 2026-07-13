using System;
using System.Globalization;
using System.Windows.Data;
using RatScanner.ViewModel;

namespace RatScanner.View;

[ValueConversion(typeof(int), typeof(string))]
public class IntToLongPriceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        MenuVM.FormatLongPrice(value as int? ?? 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => 0;
}
