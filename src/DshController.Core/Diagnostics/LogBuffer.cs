// ============================================================================
//  LogBuffer — 控制台日志的环形缓冲（重构 2.0 / P3）
//
//  原实现把 20 万字符的整段转录每 100ms 重新赋给 TextBox.Text——高频输出时
//  UI 线程每次都要重建整个字符串与文本布局（O(n) 且 n 很大）。
//  这里把"缓冲与裁剪"从界面里剥出来：按行保存、按行上限裁剪、只吐出增量，
//  界面据此增量追加（虚拟化列表 O(1)）。纯逻辑，可离线单测。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;

namespace DshController.Core.Diagnostics
{
    public sealed class LogBuffer
    {
        private readonly Queue<string> _lines = new Queue<string>();
        private readonly object _gate = new object();

        public LogBuffer(int maxLines = 2000)
        {
            MaxLines = maxLines < 1 ? 1 : maxLines;
        }

        public int MaxLines { get; }

        /// <summary>因超出上限被丢弃的行数（界面可据此提示"更早的日志已截断"）。</summary>
        public long DroppedLines { get; private set; }

        public int Count
        {
            get { lock (_gate) return _lines.Count; }
        }

        /// <summary>追加一行；返回带时间戳的成品行（界面直接拿去显示，不再自己拼字符串）。</summary>
        public string Append(string line, DateTime now)
        {
            string entry = now.ToString("HH:mm:ss") + "  " + (line ?? "");
            lock (_gate)
            {
                _lines.Enqueue(entry);
                while (_lines.Count > MaxLines)
                {
                    _lines.Dequeue();
                    DroppedLines++;
                }
            }
            return entry;
        }

        public IReadOnlyList<string> Snapshot()
        {
            lock (_gate) return new List<string>(_lines);
        }

        /// <summary>全文（复制到剪贴板用）。</summary>
        public string Text()
        {
            var sb = new StringBuilder();
            lock (_gate)
            {
                foreach (string l in _lines) sb.AppendLine(l);
            }
            return sb.ToString();
        }

        public void Clear()
        {
            lock (_gate)
            {
                _lines.Clear();
                DroppedLines = 0;
            }
        }
    }
}
