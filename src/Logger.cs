using System;
using System.IO;
using System.Text;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcCore = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MPlot
{
    /// <summary>批打日志：同时写命令行与文件。文件放在 %APPDATA%\MPlot\logs，绝不写进图纸目录。</summary>
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static string _sessionFile;
        private static bool _disabled;

        public static string SessionFile
        {
            get
            {
                if (_sessionFile == null)
                {
                    string dir = Path.Combine(FrameLibrary.EnsureDir(), "logs");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    _sessionFile = Path.Combine(dir, "MPlot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                }
                return _sessionFile;
            }
        }

        public static void ResetSession() { _sessionFile = null; }

        public static void Info(string msg) { Write("INFO ", msg, true); }
        public static void Warn(string msg) { Write("WARN ", msg, true); }
        public static void Error(string msg) { Write("ERROR", msg, true); }
        public static void Error(string msg, Exception ex)
        {
            Write("ERROR", msg + Environment.NewLine + (ex == null ? "" : ex.ToString()), true);
        }
        /// <summary>只写文件、不刷命令行，避免高频输出打断交互。</summary>
        public static void Verbose(string msg) { Write("DEBUG", msg, false); }

        private static void Write(string level, string msg, bool toConsole)
        {
            if (_disabled) return;
            try
            {
                lock (_lock)
                {
                    string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + msg;
                    File.AppendAllText(SessionFile, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { _disabled = true; }   // 日志盘不可写时绝不拖垮批打

            if (!toConsole) return;
            try
            {
                var doc = AcCore.DocumentManager.MdiActiveDocument;
                if (doc != null && doc.Editor != null) doc.Editor.WriteMessage("\nM批打: " + msg);
            }
            catch { }
        }
    }
}
