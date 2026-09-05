using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using KSubMaker.App.Resources;
using KSubMaker.App.Services;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Settings;

namespace KSubMaker.App.Converters;

/// <summary>
/// Base class for the one-way converters in this file.
///
/// <see cref="ConvertBack"/> is not "not implemented yet": these bindings are display-only, and a
/// silent <see cref="Binding.DoNothing"/> is what keeps a two-way binding mistake from throwing at
/// runtime in a released build.
/// </summary>
public abstract class OneWayConverter : IValueConverter
{
    public abstract object Convert(object? value, Type targetType, object? parameter, CultureInfo culture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Byte count to <c>3 GB</c> / <c>484 MB</c>.</summary>
public sealed class BytesToStringConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            long bytes => DisplayText.Bytes(bytes),
            int bytes => DisplayText.Bytes(bytes),
            double bytes => DisplayText.Bytes((long)bytes),
            _ => Strings.Dash
        };
}

/// <summary>Seconds (or a <see cref="TimeSpan"/>) to <c>h:mm:ss</c>.</summary>
public sealed class SecondsToTimeConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            double seconds => DisplayText.Duration(seconds),
            int seconds => DisplayText.Duration(seconds),
            long seconds => DisplayText.Duration(seconds),
            TimeSpan span => DisplayText.Duration(span),
            _ => Strings.Dash
        };
}

/// <summary><see cref="JobStatus"/> to its Korean label.</summary>
public sealed class JobStatusToTextConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is JobStatus status ? DisplayText.StatusName(status) : Strings.Dash;
}

/// <summary><see cref="ModelKind"/> to its Korean category name (모델 관리 group headers).</summary>
public sealed class ModelKindToNameConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ModelKind kind ? DisplayText.ModelKindName(kind) : Strings.Dash;
}

/// <summary><see cref="ModelKind"/> to its one-line category blurb (모델 관리 group headers).</summary>
public sealed class ModelKindToHintConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ModelKind kind ? DisplayText.ModelKindHint(kind) : string.Empty;
}


/// <summary><see cref="JobStage"/> to its Korean label.</summary>
public sealed class JobStageToTextConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is JobStage stage ? DisplayText.StageName(stage) : Strings.JobStageNone;
}

/// <summary>
/// <see cref="JobStatus"/> to the foreground brush of the 상태 cell.
///
/// The brushes are frozen: an unfrozen brush handed to thousands of virtualised rows keeps a
/// per-element change-notification subscription alive and shows up as scroll stutter.
/// </summary>
public sealed class JobStatusToBrushConverter : OneWayConverter
{
    private static readonly SolidColorBrush Neutral = Freeze(Color.FromRgb(0x40, 0x40, 0x40));
    private static readonly SolidColorBrush Active = Freeze(Color.FromRgb(0x0F, 0x62, 0xFE));
    private static readonly SolidColorBrush Success = Freeze(Color.FromRgb(0x1F, 0x7A, 0x33));
    private static readonly SolidColorBrush Danger = Freeze(Color.FromRgb(0xC2, 0x1E, 0x1E));
    private static readonly SolidColorBrush Muted = Freeze(Color.FromRgb(0x77, 0x77, 0x77));
    private static readonly SolidColorBrush Warning = Freeze(Color.FromRgb(0xB0, 0x6E, 0x00));

    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not JobStatus status
            ? Neutral
            : status switch
            {
                JobStatus.Completed => Success,
                JobStatus.Failed => Danger,
                JobStatus.Cancelled => Muted,
                JobStatus.Skipped => Muted,
                JobStatus.Paused => Warning,
                JobStatus.Pending => Neutral,
                _ => Active
            };

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Null / empty string to <see cref="Visibility.Collapsed"/>.
/// Pass <c>Invert</c> as the converter parameter to show the element only when the value is null.
/// </summary>
public sealed class NullToVisibilityConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value is null || (value is string text && string.IsNullOrWhiteSpace(text));

        if (IsInverted(parameter))
        {
            isNull = !isNull;
        }

        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    internal static bool IsInverted(object? parameter) =>
        parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Bool to <see cref="Visibility"/>. Pass <c>Invert</c> as the converter parameter to collapse on
/// true instead of on false.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;

        if (NullToVisibilityConverter.IsInverted(parameter))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility visibility && visibility == Visibility.Visible;
}

/// <summary>
/// Logical negation. Used to drive <c>IsEnabled</c> from an "자동" checkbox: the manual controls are
/// enabled exactly when automatic selection is off.
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool flag || !flag;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool flag || !flag;
}

/// <summary>Percentage (0-100) to <c>73.5%</c>.</summary>
public sealed class PercentToTextConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            double percent => DisplayText.Percent(percent),
            int percent => DisplayText.Percent(percent),
            _ => Strings.Dash
        };
}

/// <summary>Empty or null string to a dash, so a grid cell is never blank for no reason.</summary>
public sealed class EmptyToDashConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DisplayText.OrDash(value as string);
}

/// <summary>
/// True when the bound value equals the converter parameter, compared as text. Drives the checkmark
/// on the 테스트 실행 length menu so the remembered choice is visible without a label on the button.
/// </summary>
public sealed class ValueEqualsParameterConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(
            value?.ToString(),
            parameter?.ToString(),
            StringComparison.Ordinal);
}
