using System.Globalization;
using UnifiedAudio.Helpers;
using UnifiedAudio.Models;

namespace UnifiedAudio.Models
{
    // The UI helpers are linked directly so these tests exercise the shipped catalogs.
    public enum AppLanguage { System, English, Spanish, SimplifiedChinese }
}

namespace UnifiedAudio.Core.Tests
{
    public sealed class LocalizationTests
    {
        [Theory]
        [InlineData("en-US", AppLanguage.System, "Mode")]
        [InlineData("es-MX", AppLanguage.System, "Modo")]
        [InlineData("zh-CN", AppLanguage.System, "模式")]
        [InlineData("es-MX", AppLanguage.English, "Mode")]
        [InlineData("en-US", AppLanguage.Spanish, "Modo")]
        public void SystemAndExplicitLanguagesStayConsistent(string culture, AppLanguage language, string expected)
        {
            var previous = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = new CultureInfo(culture);
                Loc.Language = language;
                Assert.Equal(expected, LiteralCatalog.Get("Modo"));
                if (expected == "Mode")
                    Assert.StartsWith("Changes apply automatically.", LiteralCatalog.Get("Los cambios se aplican automáticamente. Voz: micrófono procesado. PC: salida seleccionada. Final: audio enviado al micrófono virtual."));
            }
            finally { CultureInfo.CurrentUICulture = previous; Loc.Language = AppLanguage.System; }
        }

        [Fact]
        public void UiSourcesContainNoMojibake()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "UnifiedAudio.slnx"))) root = root.Parent;
            Assert.NotNull(root);
            var app = Path.Combine(root.FullName, "src", "UnifiedAudio.App");
            foreach (var path in Directory.EnumerateFiles(app, "*", SearchOption.AllDirectories)
                .Where(p => (p.EndsWith(".cs") || p.EndsWith(".xaml"))
                    && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
            {
                var source = File.ReadAllText(path, new System.Text.UTF8Encoding(false, true));
                foreach (var marker in new[] { "\u00c3\u00a9", "\u00c3\u00b3", "\u00c2\u00b7", "\u00e2\u20ac", "\ufffd" })
                    Assert.False(source.Contains(marker), $"Encoding damage in {path}");
            }
        }
    }
}
