using System;
using System.IO;

namespace FluxCore
{
    public class FluxLogger
    {
        private string _path;
        public FluxLogger()
        {
            _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FluxDebug.txt");
        }

        public void Log(string message)
        {
            try
            {
                File.AppendAllText(_path, $"[{DateTime.Now:HH:mm:ss}] {message}\n");
            }
            catch { }
        }
    }
}
