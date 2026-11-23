using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace CodexPluginSync.Core
{
    /// <summary>
    /// Append-only log writer (§6.5). Never rotates, never throws into the caller:
    /// a failure to log must not fail the operation.
    /// </summary>
    public sealed class Logger : IDisposable
    {
        private readonly object _gate = new object();
        private StreamWriter _writer;
        private string _path;

        public Logger(string logDirectory, string fileNamePrefix)
        {
            try
            {
                string directory = logDirectory;
                Directory.CreateDirectory(directory);
                _path = System.IO.Path.Combine(directory, fileNamePrefix + "-" + CodexPaths.Stamp() + ".log");
                FileStream stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, new UTF8Encoding(false));
                _writer.AutoFlush = true;
            }
            catch (Exception)
            {
                _writer = null;
                _path = null;
            }
        }

        /// <summary>Null when the log file could not be opened (read-only CodexHome, etc.).</summary>
        public string FilePath
        {
            get { return _path; }
        }

        public void Info(string phase, string fields)
        {
            Write("INFO", phase, fields);
        }

        public void Warn(string phase, string fields)
        {
            Write("WARN", phase, fields);
        }

        public void Error(string phase, string fields)
        {
            Write("ERROR", phase, fields);
        }

        private void Write(string level, string phase, string fields)
        {
            if (_writer == null)
            {
                return;
            }
            string line = string.Format(CultureInfo.InvariantCulture, "[{0}] {1} phase={2}{3}",
                DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), level, phase,
                string.IsNullOrEmpty(fields) ? string.Empty : " " + fields);
            lock (_gate)
            {
                try
                {
                    _writer.WriteLine(line);
                }
                catch (Exception)
                {
                    // Logging must never break the operation.
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_writer != null)
                {
                    try
                    {
                        _writer.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                    _writer = null;
                }
            }
        }
    }
}
