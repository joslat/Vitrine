// SPDX-License-Identifier: MIT

using System.Globalization;
using Avalonia.Data.Converters;

namespace AgentEval.VitrineDemo.App.ViewModels;

/// <summary>Keeps the internal compatibility enum while presenting intent-revealing UI copy.</summary>
public sealed class RunModeDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is VitrineRunMode.Ablation
            ? "Catalogue integrity self-test"
            : value?.ToString() ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The run-mode item template is display-only.");
}
