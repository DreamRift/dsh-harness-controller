// ============================================================================
//  迁移的边界分支（重构 2.0 / P0.5）
//
//  真实踩坑场景：用户先运行了一次"全新构建产物"（状态目录里生成空清单），
//  之后才把新版本部署到旧目录；如果迁移一律"目标存在即跳过"，旧实例清单
//  就永远导不进来。这里锁定该分支的行为与保护条件。
// ============================================================================

using System;
using System.IO;
using DshController.Core.Storage;
using Xunit;

namespace DshController.Tests
{
    public class AppPathsMigrationEdgeTests : IDisposable
    {
        private readonly string _tmp;

        public AppPathsMigrationEdgeTests()
        {
            _tmp = Path.Combine(Path.GetTempPath(), "dsh-tests-mig-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_tmp);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true); }
            catch { /* 理由: 临时目录清理失败不应让测试结果失真 */ }
        }

        private string[] Prepare(string targetJson, string sourceJson)
        {
            string src = Path.Combine(_tmp, "exe");
            string dst = Path.Combine(_tmp, "state");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);
            if (sourceJson != null) File.WriteAllText(Path.Combine(src, "instances.json"), sourceJson);
            if (targetJson != null) File.WriteAllText(Path.Combine(dst, "instances.json"), targetJson);
            return new[] { src, dst };
        }

        [Fact]
        public void 目标为空清单而源非空时接管并备份()
        {
            string[] p = Prepare("{\"version\":2,\"instances\":[]}",
                                 "{\"version\":2,\"instances\":[{\"id\":\"a\"}]}");

            Assert.True(AppPaths.MigrateFrom(p[0], p[1]).Migrated);
            Assert.Equal(1, AppPaths.CountInstances(Path.Combine(p[1], "instances.json")));
            Assert.NotEmpty(Directory.GetFiles(p[1], "instances.json.replaced-*"));
        }

        [Fact]
        public void 目标非空时绝不接管()
        {
            string[] p = Prepare("{\"version\":2,\"instances\":[{\"id\":\"keep\"}]}",
                                 "{\"version\":2,\"instances\":[{\"id\":\"a\"},{\"id\":\"b\"}]}");

            Assert.False(AppPaths.MigrateFrom(p[0], p[1]).Migrated);
            Assert.Equal(1, AppPaths.CountInstances(Path.Combine(p[1], "instances.json")));
        }

        [Fact]
        public void 目标损坏时按非空保守处理()
        {
            string[] p = Prepare("{ broken", "{\"version\":2,\"instances\":[{\"id\":\"a\"}]}");

            Assert.False(AppPaths.MigrateFrom(p[0], p[1]).Migrated);
            Assert.Equal("{ broken", File.ReadAllText(Path.Combine(p[1], "instances.json")));
        }

        [Fact]
        public void 实例计数语义()
        {
            Assert.Equal(-1, AppPaths.CountInstances(Path.Combine(_tmp, "missing.json")));
            string bad = Path.Combine(_tmp, "bad.json");
            File.WriteAllText(bad, "not json");
            Assert.Equal(-2, AppPaths.CountInstances(bad));
            string ok = Path.Combine(_tmp, "ok.json");
            File.WriteAllText(ok, "{\"instances\":[{\"id\":\"a\"},{\"id\":\"b\"}]}");
            Assert.Equal(2, AppPaths.CountInstances(ok));
        }
    }
}
