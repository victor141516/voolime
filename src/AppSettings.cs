using Microsoft.Win32;

namespace Voolime;

internal static class AppSettings
{
    private const string KeyPath = @"Software\Voolime";
    private const string ActivationModifierValue = "ActivationModifier";

    public static ActivationModifier LoadActivationModifier()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        var value = key?.GetValue(ActivationModifierValue) as string;
        return Enum.TryParse<ActivationModifier>(value, ignoreCase: true, out var modifier)
            ? modifier
            : ActivationModifier.Shift;
    }

    public static void SaveActivationModifier(ActivationModifier modifier)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key?.SetValue(ActivationModifierValue, modifier.ToString(), RegistryValueKind.String);
    }
}
