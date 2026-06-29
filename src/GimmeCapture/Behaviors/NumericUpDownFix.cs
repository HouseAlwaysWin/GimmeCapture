using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GimmeCapture.Behaviors;

/// <summary>
/// Makes Avalonia's <see cref="NumericUpDown"/> robust for two long-standing quirks, applied app-wide via a
/// global style (<c>behaviors:NumericUpDownFix.Enable="True"</c>):
///
///  • <b>Null on empty</b> — clearing the field sets <c>Value</c> to <c>null</c>, which throws
///    (<c>InvalidCastException: Could not convert '(null)' to System.Decimal</c>) on a two-way binding to a
///    non-nullable <c>decimal</c>. We restore the previous value (deferred, and only if the field is still
///    empty) so a transient null never sticks — without fighting the normal "select-all then type" flow.
///  • <b>Enter doesn't commit</b> — pressing Enter leaves the inner TextBox focused and "uncommitted". We
///    clear focus on Enter, which commits the typed value and blurs the field.
/// </summary>
public static class NumericUpDownFix
{
    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<NumericUpDown, bool>("Enable", typeof(NumericUpDownFix));

    public static void SetEnable(NumericUpDown element, bool value) => element.SetValue(EnableProperty, value);

    public static bool GetEnable(NumericUpDown element) => element.GetValue(EnableProperty);

    static NumericUpDownFix()
    {
        EnableProperty.Changed.AddClassHandler<NumericUpDown>((nud, e) =>
        {
            nud.ValueChanged -= OnValueChanged;
            nud.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);

            if (e.GetNewValue<bool>())
            {
                nud.ValueChanged += OnValueChanged;
                // Tunnel so we see Enter before the inner TextBox marks it handled.
                nud.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            }
        });
    }

    private static void OnValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (e.NewValue is not null || sender is not NumericUpDown nud)
        {
            return;
        }

        decimal restore = e.OldValue ?? Math.Max(0m, nud.Minimum);
        Dispatcher.UIThread.Post(
            () =>
            {
                if (nud.Value is null) // still empty (the user didn't type a replacement) → put the value back
                {
                    nud.Value = restore;
                }
            },
            DispatcherPriority.Background);
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not NumericUpDown nud)
        {
            return;
        }

        // Blur the inner TextBox (which commits the typed value) by focusing a focusable ancestor, else the
        // window — there's no public commit/clear-focus API on NumericUpDown in this Avalonia build.
        Visual? v = nud.GetVisualParent();
        while (v != null)
        {
            if (v is InputElement { Focusable: true } target)
            {
                target.Focus();
                break;
            }
            v = v.GetVisualParent();
        }
        if (v == null)
        {
            (TopLevel.GetTopLevel(nud) as IInputElement)?.Focus();
        }

        e.Handled = true;
    }
}
