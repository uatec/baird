using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Baird.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Baird.Tests
{
    public class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }

    public class TestApplication : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
        }
    }
}
