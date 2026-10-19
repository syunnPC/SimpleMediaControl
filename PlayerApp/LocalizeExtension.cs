using Microsoft.UI.Xaml.Markup;

namespace PlayerApp;

// Resolve initial XAML text during construction, before live media updates.
// No binding later overwrites song metadata or lyrics when the window loads.
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed class LocalizeExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    protected override object ProvideValue() => UiText.Get(Key);
}
