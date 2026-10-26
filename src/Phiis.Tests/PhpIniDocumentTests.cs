using System.Linq;
using Phiis.Core;
using Xunit;

namespace Phiis.Tests
{
    public class PhpIniDocumentTests
    {
        private const string Sample =
            "[PHP]\n" +
            "; comment\n" +
            "display_errors = On\n" +
            ";date.timezone =\n" +
            "extension_dir = \"ext\" ; inline comment\n" +
            ";extension=curl\n" +
            "extension=php_mbstring.dll\n" +
            ";zend_extension=opcache\n" +
            "\n" +
            "[Date]\n" +
            "; other section\n";

        [Theory]
        [InlineData("curl", "curl")]
        [InlineData("php_curl", "curl")]
        [InlineData("php_curl.dll", "curl")]
        [InlineData("\"C:\\php\\ext\\php_curl.dll\"", "curl")]
        [InlineData("PHP_MBSTRING.DLL", "mbstring")]
        public void NormalizesExtensionNames(string input, string expected)
        {
            Assert.Equal(expected, PhpIniDocument.NormalizeExtensionName(input));
        }

        [Fact]
        public void ReadsValuesIgnoringQuotesAndInlineComments()
        {
            var ini = PhpIniDocument.FromText(Sample);
            Assert.Equal("ext", ini.GetValue("extension_dir"));
            Assert.Equal("On", ini.GetValue("display_errors"));
            Assert.Null(ini.GetValue("date.timezone"));
        }

        [Fact]
        public void SetReplacesActiveValueAndIsIdempotent()
        {
            var ini = PhpIniDocument.FromText(Sample);
            Assert.True(ini.SetValue("display_errors", "Off"));
            Assert.False(ini.SetValue("display_errors", "Off"));
            Assert.Equal("Off", ini.GetValue("display_errors"));
            Assert.Single(ini.Lines, l => l.StartsWith("display_errors"));
        }

        [Fact]
        public void SetPlacesNewValueNextToCommentedExample()
        {
            var ini = PhpIniDocument.FromText(Sample);
            ini.SetValue("date.timezone", "Asia/Kuala_Lumpur");
            var lines = ini.Lines.ToList();
            Assert.Equal("date.timezone = Asia/Kuala_Lumpur", lines[lines.IndexOf(";date.timezone =") + 1]);
        }

        [Fact]
        public void SetAppendsUnknownDirectiveInsidePhpSection()
        {
            var ini = PhpIniDocument.FromText(Sample);
            ini.SetValue("cgi.fix_pathinfo", "1");
            var lines = ini.Lines.ToList();
            Assert.True(lines.IndexOf("cgi.fix_pathinfo = 1") < lines.IndexOf("[Date]"));
        }

        [Fact]
        public void EnableUncommentsExistingLineInsteadOfDuplicating()
        {
            var ini = PhpIniDocument.FromText(Sample);
            Assert.True(ini.EnableExtension("php_curl.dll", false));
            Assert.False(ini.EnableExtension("curl", false));
            Assert.Single(ini.GetExtensions(), e => e.Name == "curl" && e.Enabled);
            Assert.Contains("extension=curl", ini.Lines);
        }

        [Fact]
        public void EnableRecognisesOldStyleNameAsAlreadyEnabled()
        {
            var ini = PhpIniDocument.FromText(Sample);
            Assert.False(ini.EnableExtension("mbstring", false));
        }

        [Fact]
        public void EnableAddsNewExtensionAfterLastExtensionLine()
        {
            var ini = PhpIniDocument.FromText(Sample);
            Assert.True(ini.EnableExtension("intl", false));
            var lines = ini.Lines.ToList();
            Assert.Equal(lines.IndexOf(";zend_extension=opcache") + 1, lines.IndexOf("extension=intl"));
        }

        [Fact]
        public void EnableZendExtensionKeepsZendDirective()
        {
            var ini = PhpIniDocument.FromText(Sample);
            Assert.True(ini.EnableExtension("opcache", true));
            Assert.Contains("zend_extension=opcache", ini.Lines);
        }

        [Fact]
        public void DisableCommentsOutEveryActiveDuplicate()
        {
            var ini = PhpIniDocument.FromText(Sample + "extension=mbstring\n");
            Assert.Equal(2, ini.GetExtensions().Count(e => e.Name == "mbstring" && e.Enabled));
            Assert.True(ini.DisableExtension("mbstring"));
            Assert.DoesNotContain(ini.GetExtensions(), e => e.Name == "mbstring" && e.Enabled);
        }

        [Fact]
        public void UnchangedDocumentRoundTripsExactly()
        {
            var ini = PhpIniDocument.FromText(Sample);
            Assert.Equal(Sample.Replace("\n", "\r\n"), ini.ToText());
        }
    }
}
