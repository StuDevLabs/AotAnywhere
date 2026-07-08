using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace AotAnywhere.MSBuild.Tests;

// Binds the MSBuild API to the installed .NET SDK's own MSBuild before any
// Microsoft.Build type is touched. This file must reference ONLY
// Microsoft.Build.Locator - loading a Microsoft.Build type here would defeat
// the point (the locator has to run first).
internal static class MSBuildInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }
}
