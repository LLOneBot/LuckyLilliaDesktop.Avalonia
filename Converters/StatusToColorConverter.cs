using Avalonia.Data.Converters;
using Avalonia.Media;
using LuckyLilliaDesktop.Models;
using System;
using System.Globalization;

namespace LuckyLilliaDesktop.Converters;

public class StatusToColorConverter : IValueConverter
{
    public static StatusToColorConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ProcessStatus status)
        {
            return status switch
            {
                ProcessStatus.Running => new SolidColorBrush(Color.Parse("#10B981")), // Green
                ProcessStatus.Starting => new SolidColorBrush(Color.Parse("#F59E0B")), // Yellow
                ProcessStatus.Stopping => new SolidColorBrush(Color.Parse("#F59E0B")), // Yellow
                ProcessStatus.Stopped => new SolidColorBrush(Color.Parse("#6B7280")), // Gray
                _ => new SolidColorBrush(Color.Parse("#6B7280")) // Gray
            };
        }
        return new SolidColorBrush(Color.Parse("#6B7280")); // Gray
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
