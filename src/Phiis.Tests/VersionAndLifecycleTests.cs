using System;
using Phiis.Core;
using Xunit;

namespace Phiis.Tests
{
    public class VersionAndLifecycleTests
    {
        [Theory]
        [InlineData("8.4.12", "8.4.12", "")]
        [InlineData("8.5.0RC2", "8.5.0", "RC2")]
        [InlineData("7.4", "7.4.0", "")]
        [InlineData("8.3.1.0", "8.3.1", "")]
        public void ParsesPhpVersions(string text, string expected, string expectedSuffix)
        {
            Version version;
            string suffix;
            Assert.True(PhpVersionParser.TryParse(text, out version, out suffix));
            Assert.Equal(new Version(expected), version);
            Assert.Equal(expectedSuffix, suffix);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("garbage")]
        public void RejectsInvalidVersionsWithoutThrowing(string text)
        {
            Version version;
            string suffix;
            Assert.False(PhpVersionParser.TryParse(text, out version, out suffix));
        }

        [Fact]
        public void EmbeddedLifecycleDataLoads()
        {
            DateTime eol;
            Assert.True(PhpLifecycle.Load().TryGetEndOfLife("8.4", out eol));
        }

        [Fact]
        public void LifecycleStatuses()
        {
            var data = PhpLifecycle.Parse("{\"updated\":\"x\",\"branches\":[{\"branch\":\"8.2\",\"eol\":\"2026-12-31\"}]}");
            Assert.Equal(LifecycleStatus.Supported, data.GetStatus("8.2", new DateTime(2026, 1, 1)));
            Assert.Equal(LifecycleStatus.EndingSoon, data.GetStatus("8.2", new DateTime(2026, 10, 6)));
            Assert.Equal(LifecycleStatus.EndOfLife, data.GetStatus("8.2", new DateTime(2027, 1, 1)));
            // A branch newer than the data is "unknown", never "obsolete".
            Assert.Equal(LifecycleStatus.Unknown, data.GetStatus("9.0", new DateTime(2027, 1, 1)));
        }
    }
}
