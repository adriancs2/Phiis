using System;
using System.IO;
using System.Linq;
using Phiis.Core;
using Xunit;

namespace Phiis.Tests
{
    public class VcRuntimeTests : IDisposable
    {
        private readonly string _windows = Path.Combine(Path.GetTempPath(), "phiis-win-" + Guid.NewGuid().ToString("N"));

        public VcRuntimeTests()
        {
            Directory.CreateDirectory(Path.Combine(_windows, "System32"));
            Directory.CreateDirectory(Path.Combine(_windows, "SysWOW64"));
            Directory.CreateDirectory(Path.Combine(_windows, "WinSxS"));
        }

        public void Dispose()
        {
            Directory.Delete(_windows, true);
        }

        private static PhpBuild Build(int linkerMajor, string arch)
        {
            return new PhpBuild { LinkerMajor = linkerMajor, LinkerMinor = 0, Architecture = arch };
        }

        private Finding Single(PhpBuild build)
        {
            return Doctor.CheckVcRuntime(build, _windows, true).Single();
        }

        private void Touch(params string[] parts)
        {
            var path = Path.Combine(new[] { _windows }.Concat(parts).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "");
        }

        [Fact]
        public void Vc11MissingIsError()
        {
            var f = Single(Build(11, "x86"));
            Assert.Equal(Severity.Error, f.Severity);
            Assert.Contains("msvcr110.dll", f.Message);
        }

        [Fact]
        public void Vc11X86IsFoundInSysWow64()
        {
            Touch("SysWOW64", "msvcr110.dll");
            Assert.Equal(Severity.Ok, Single(Build(11, "x86")).Severity);
        }

        [Fact]
        public void Vc11X64IsFoundInSystem32()
        {
            Touch("System32", "msvcr110.dll");
            Assert.Equal(Severity.Ok, Single(Build(11, "x64")).Severity);
            Assert.Equal(Severity.Error, Single(Build(11, "x86")).Severity);
        }

        [Fact]
        public void Vc9MissingIsError()
        {
            var f = Single(Build(9, "x86"));
            Assert.Equal(Severity.Error, f.Severity);
            Assert.Contains("msvcr90.dll", f.Message);
        }

        [Fact]
        public void Vc9IsFoundInWinSxS()
        {
            Touch("WinSxS", "x86_microsoft.vc90.crt_1fc8b3b9a1e18e3b_9.0.30729.6161_none_50934f2ebcb7eb57", "msvcr90.dll");
            Assert.Equal(Severity.Ok, Single(Build(9, "x86")).Severity);
            Assert.Equal(Severity.Error, Single(Build(9, "x64")).Severity);
        }

        [Fact]
        public void Vc6IsAlwaysOk()
        {
            Assert.Equal(Severity.Ok, Single(Build(6, "x86")).Severity);
        }

        [Fact]
        public void Vc14MissingIsError()
        {
            var f = Single(Build(14, "x64"));
            Assert.Equal(Severity.Error, f.Severity);
            Assert.Contains("vcruntime140.dll", f.Message);
        }

        [Fact]
        public void UnknownToolsetIsInfo()
        {
            Assert.Equal(Severity.Info, Single(Build(8, "x86")).Severity);
        }
    }
}
