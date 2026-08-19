// Licensed under GPL-v3.0
using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Kodo.Models;

/// <summary>
/// Inverts a boolean value. Used to toggle visibility of mutually
/// exclusive layouts inside a single DataTemplate.
/// </summary>
public sealed class BoolInverter : IValueConverter
{
    public static readonly BoolInverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : value;
}
