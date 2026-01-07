using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace LuckyLilliaDesktop.Converters;

/// <summary>
/// 将整数与参数进行相等性比较的转换器
/// </summary>
public class EqualToIntConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue && parameter is string paramStr && int.TryParse(paramStr, out int paramInt))
        {
            return intValue == paramInt;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
