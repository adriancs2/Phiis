using System.Linq;
using Phiis.Core;
using Xunit;

namespace Phiis.Tests
{
    public class PhpIniDirectiveTests
    {
        private const string Sample =
            "[PHP]\r\n" +
            "; Maximum amount of memory a script may consume\r\n" +
            "; https://php.net/memory-limit\r\n" +
            "memory_limit = 128M ; keep this low\r\n" +
            "\r\n" +
            "; https://php.net/error-reporting\r\n" +
            "error_reporting = E_ALL & ~E_DEPRECATED & ~E_STRICT\r\n" +
            "extension=curl\r\n" +
            ";extension=intl\r\n" +
            "\r\n" +
            "[Date]\r\n" +
            "; Defines the default timezone used by the date functions\r\n" +
            ";date.timezone =\r\n" +
            "\r\n" +
            "[HOST=www.example.com]\r\n" +
            "memory_limit = 512M\r\n";

        private static PhpIniDocument Doc()
        {
            return PhpIniDocument.FromText(Sample);
        }

        [Fact]
        public void ListsDirectivesWithDescriptionsAndSkipsExtensions()
        {
            var all = Doc().GetDirectives();
            Assert.DoesNotContain(all, d => d.Name == "extension");

            var memory = all.Single(d => d.Name == "memory_limit" && !d.IsPerScope);
            Assert.Equal("128M", memory.Value);
            Assert.Contains("Maximum amount of memory", memory.Description);

            var tz = all.Single(d => d.Name == "date.timezone");
            Assert.Null(tz.Value);
            Assert.Equal("", tz.CommentedValue);
            Assert.Equal("Date", tz.Section);
        }

        [Fact]
        public void ExpressionValuesAreReadWhole()
        {
            Assert.Equal("E_ALL & ~E_DEPRECATED & ~E_STRICT", Doc().GetValue("error_reporting"));
        }

        [Fact]
        public void PerHostDirectivesAreListedSeparatelyAndIgnoredForGlobalValues()
        {
            var doc = Doc();
            var host = doc.GetDirectives().Single(d => d.Name == "memory_limit" && d.IsPerScope);
            Assert.Equal("512M", host.Value);
            Assert.Equal("HOST=www.example.com", host.Section);
            Assert.Equal("128M", doc.GetValue("memory_limit"));
        }

        [Fact]
        public void SetValueKeepsInlineCommentAndLeavesPerHostLineAlone()
        {
            var doc = Doc();
            Assert.True(doc.SetValue("memory_limit", "256M"));
            Assert.Contains("memory_limit = 256M ; keep this low", doc.Lines);
            Assert.Contains("memory_limit = 512M", doc.Lines);
        }

        [Fact]
        public void SetValueOfCommentedDirectiveGoesBelowItsDefault()
        {
            var doc = Doc();
            doc.SetValue("date.timezone", "Asia/Kuala_Lumpur");
            var lines = doc.Lines.ToList();
            Assert.Equal(lines.IndexOf(";date.timezone =") + 1, lines.IndexOf("date.timezone = Asia/Kuala_Lumpur"));
        }

        [Fact]
        public void NewDirectiveWithoutPhpSectionIsNotAppendedIntoHostSection()
        {
            var doc = PhpIniDocument.FromText("memory_limit = 128M\r\n\r\n[HOST=www.example.com]\r\nmemory_limit = 512M\r\n");
            doc.SetValue("max_execution_time", "60");
            var lines = doc.Lines.ToList();
            Assert.True(lines.IndexOf("max_execution_time = 60") < lines.IndexOf("[HOST=www.example.com]"));
        }

        [Fact]
        public void ValueWithSemicolonIsQuoted()
        {
            var doc = Doc();
            doc.SetValue("include_path", ".;C:\\php\\includes");
            Assert.Equal(".;C:\\php\\includes", doc.GetValue("include_path"));
        }

        [Fact]
        public void RemoveValueDoesNotTouchPerHostLine()
        {
            var doc = Doc();
            Assert.True(doc.RemoveValue("memory_limit"));
            Assert.Null(doc.GetValue("memory_limit"));
            Assert.Contains("memory_limit = 512M", doc.Lines);
        }
    }
}
