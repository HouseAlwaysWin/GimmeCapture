using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace GimmeCapture.Behaviors;

/// <summary>
/// Turns a panel into a single-open accordion: whenever one of its direct child <see cref="Expander"/>s
/// expands, the others collapse — so at most one group is open at a time. Set
/// <c>behaviors:AccordionGroup.IsContainer="True"</c> on the panel that hosts the Expanders.
///
/// The default-open group stays declarative in XAML (put <c>IsExpanded="True"</c> on the first Expander
/// only); this behavior never opens a group on its own, it only collapses the non-active ones. Keeping the
/// default out of here avoids any init-ordering race with Avalonia's lazy-loaded tabs.
/// </summary>
public static class AccordionGroup
{
    public static readonly AttachedProperty<bool> IsContainerProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsContainer", typeof(AccordionGroup));

    public static void SetIsContainer(Control element, bool value) => element.SetValue(IsContainerProperty, value);

    public static bool GetIsContainer(Control element) => element.GetValue(IsContainerProperty);

    // Live IsExpanded subscriptions per container. A TabControl detaches/re-attaches tab content on
    // selection, so we rebuild subscriptions on every visual-tree attach and dispose them on detach.
    private static readonly ConditionalWeakTable<Control, List<IDisposable>> Subscriptions = new();

    static AccordionGroup()
    {
        IsContainerProperty.Changed.AddClassHandler<Control>((container, e) =>
        {
            container.AttachedToVisualTree -= OnAttached;
            container.DetachedFromVisualTree -= OnDetached;

            if (e.GetNewValue<bool>())
            {
                // IsContainer is set in XAML before the panel is attached, so wiring happens on the
                // AttachedToVisualTree event (which also re-fires when a TabControl re-shows the tab).
                container.AttachedToVisualTree += OnAttached;
                container.DetachedFromVisualTree += OnDetached;
            }
            else
            {
                Unwire(container);
            }
        });
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control container)
        {
            Wire(container);
        }
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control container)
        {
            Unwire(container);
        }
    }

    private static void Wire(Control container)
    {
        Unwire(container);

        var subscriptions = new List<IDisposable>();
        foreach (var expander in GetChildExpanders(container))
        {
            var captured = expander;
            // GetObservable pushes the current value on subscribe, so the declarative open group keeps
            // its state; only an expand (true) collapses the rest.
            subscriptions.Add(captured.GetObservable(Expander.IsExpandedProperty).Subscribe(isExpanded =>
            {
                if (isExpanded)
                {
                    CollapseOthers(container, captured);
                }
            }));
        }

        Subscriptions.AddOrUpdate(container, subscriptions);
    }

    private static void Unwire(Control container)
    {
        if (Subscriptions.TryGetValue(container, out var subscriptions))
        {
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }

            Subscriptions.Remove(container);
        }
    }

    private static void CollapseOthers(Control container, Expander active)
    {
        foreach (var expander in GetChildExpanders(container))
        {
            if (!ReferenceEquals(expander, active) && expander.IsExpanded)
            {
                expander.IsExpanded = false;
            }
        }
    }

    private static IEnumerable<Expander> GetChildExpanders(Control container)
    {
        if (container is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is Expander expander)
                {
                    yield return expander;
                }
            }
        }
    }
}
