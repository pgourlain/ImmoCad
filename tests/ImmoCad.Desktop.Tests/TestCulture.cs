using System.Globalization;
using System.Runtime.CompilerServices;

namespace ImmoCad.Desktop.Tests;

/// <summary>The assertions compare numbers written with a dot; run them whatever the regional settings of the machine.</summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void Init()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }
}
