// ============================================================================
//  LogBuffer 的离线单测（重构 2.0 / P3）
//
//  日志缓冲原本长在 MainWindow 里（20 万字符转录 + 每 100ms 整体回写），
//  抽出来之后裁剪与丢弃语义就可以被直接验证。
// ============================================================================

using System;
using DshController.Core.Diagnostics;
using Xunit;

namespace DshController.Tests
{
    public class LogBufferTests
    {
        private static readonly DateTime T = new DateTime(2026, 9, 1, 20, 30, 5, DateTimeKind.Local);

        [Fact]
        public void 追加返回带时间戳的成品行()
        {
            var buf = new LogBuffer();
            Assert.Equal("20:30:05  后端已启动", buf.Append("后端已启动", T));
            Assert.Equal(1, buf.Count);
        }

        [Fact]
        public void 超过上限时丢弃最旧的行()
        {
            var buf = new LogBuffer(maxLines: 3);
            for (int i = 1; i <= 5; i++) buf.Append("line" + i, T);

            Assert.Equal(3, buf.Count);
            Assert.Equal(2, buf.DroppedLines);
            Assert.Equal(new[] { "20:30:05  line3", "20:30:05  line4", "20:30:05  line5" }, buf.Snapshot());
        }

        [Fact]
        public void 上限至少为一行()
        {
            var buf = new LogBuffer(maxLines: 0);
            buf.Append("a", T);
            buf.Append("b", T);
            Assert.Equal(1, buf.Count);
        }

        [Fact]
        public void 全文用于复制到剪贴板()
        {
            var buf = new LogBuffer();
            buf.Append("a", T);
            buf.Append("b", T);
            Assert.Contains("20:30:05  a", buf.Text());
            Assert.Contains("20:30:05  b", buf.Text());
        }

        [Fact]
        public void 清空同时复位丢弃计数()
        {
            var buf = new LogBuffer(maxLines: 1);
            buf.Append("a", T);
            buf.Append("b", T);
            Assert.Equal(1, buf.DroppedLines);

            buf.Clear();

            Assert.Equal(0, buf.Count);
            Assert.Equal(0, buf.DroppedLines);
            Assert.Equal("", buf.Text());
        }

        [Fact]
        public void 空行与null不抛()
        {
            var buf = new LogBuffer();
            Assert.EndsWith("  ", buf.Append(null, T));
            Assert.Equal(1, buf.Count);
        }
    }
}
