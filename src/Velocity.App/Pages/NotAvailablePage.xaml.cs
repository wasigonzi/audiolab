using Microsoft.UI.Xaml.Controls;

namespace Velocity.App.Pages;

/// <summary>Shown for a route whose page belongs to a later phase.</summary>
/// <remarks>
/// An explicit, honest placeholder rather than an empty page or a crash: the sidebar mirrors the
/// engine's categories, and some of those categories genuinely have no page yet.
/// </remarks>
public sealed partial class NotAvailablePage : Page
{
    /// <summary>Creates the page.</summary>
    public NotAvailablePage() => InitializeComponent();
}
