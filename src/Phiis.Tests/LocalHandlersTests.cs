using Phiis.Core;
using Xunit;

namespace Phiis.Tests
{
    public class LocalHandlersTests
    {
        private static readonly string[] ServerPhp = { "PHP_via_FastCGI_8.4_x64", "PHP_via_FastCGI_8.5_x64" };

        private static LocalHandler Php(string name)
        {
            return new LocalHandler { Name = name, Path = "*.php", Modules = "FastCgiModule", ScriptProcessor = @"C:\php\php-cgi.exe" };
        }

        [Fact]
        public void PinnedDeltaIsOnlyPhp()
        {
            var local = new LocalHandlers();
            local.Adds.Add(Php("Phiis_PHP_8.5_x64"));
            Assert.True(local.OnlyPhp(ServerPhp));
        }

        [Fact]
        public void LeftoverRemovesOfServerPhpAreOnlyPhp()
        {
            var local = new LocalHandlers();
            local.Removes.Add("PHP_via_FastCGI_8.4_x64");
            Assert.True(local.OnlyPhp(ServerPhp));
        }

        [Fact]
        public void OtherHandlerCustomisationsAreKept()
        {
            var local = new LocalHandlers();
            local.Adds.Add(Php("Phiis_PHP_8.5_x64"));
            local.Adds.Add(new LocalHandler { Name = "MyHandler", Path = "*.xyz", Modules = "IsapiModule" });
            Assert.False(local.OnlyPhp(ServerPhp));

            var removesOther = new LocalHandlers();
            removesOther.Removes.Add("WebDAV");
            Assert.False(removesOther.OnlyPhp(ServerPhp));
        }

        [Fact]
        public void FullCopyIsNeverOnlyPhp()
        {
            var local = new LocalHandlers { HasClear = true };
            local.Adds.Add(Php("PHP_via_FastCGI_8.4_x64"));
            Assert.False(local.OnlyPhp(ServerPhp));
        }
    }
}
