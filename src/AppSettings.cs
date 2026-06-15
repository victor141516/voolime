using Microsoft.Win32;

namespace Voolime;

internal static class AppSettings
{
    private const string KeyPath = @"Software\Voolime";
    private const string ActivationModifierValue = "ActivationModifier";
    private const string KeyboardActivationModifiersValue = "KeyboardActivationModifiers";
    private const string MouseActivationModifiersValue = "MouseActivationModifiers";

    public static ActivationModifiers LoadKeyboardActivationModifiers()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        var value = key?.GetValue(KeyboardActivationModifiersValue) as string;
        if (Enum.TryParse<ActivationModifiers>(value, ignoreCase: true, out var modifiers))
        {
            return modifiers;
        }

        var legacyValue = key?.GetValue(ActivationModifierValue) as string;
        return Enum.TryParse<ActivationModifiers>(legacyValue, ignoreCase: true, out var modifier)
            ? modifier
            : ActivationModifiers.Shift;
    }

    public static void SaveKeyboardActivationModifiers(ActivationModifiers modifiers) =>
        SaveModifiers(KeyboardActivationModifiersValue, modifiers);

    public static ActivationModifiers LoadMouseActivationModifiers()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        var value = key?.GetValue(MouseActivationModifiersValue) as string;
        return Enum.TryParse<ActivationModifiers>(value, ignoreCase: true, out var modifiers)
            ? modifiers
            : ActivationModifiers.Control | ActivationModifiers.Shift;
    }

    public static void SaveMouseActivationModifiers(ActivationModifiers modifiers) =>
        SaveModifiers(MouseActivationModifiersValue, modifiers);

    private static void SaveModifiers(string valueName, ActivationModifiers modifiers)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key?.SetValue(valueName, modifiers.ToString(), RegistryValueKind.String);
    }
}
