// SPDX-License-Identifier: MIT

using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Skia;
using VitrineApplication = AgentEval.VitrineDemo.App.App;

[assembly: AvaloniaTestApplication(typeof(AgentEval.VitrineDemo.Tests.AvaloniaTestHost))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AgentEval.VitrineDemo.Tests;

public static class AvaloniaTestHost
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<VitrineApplication>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
