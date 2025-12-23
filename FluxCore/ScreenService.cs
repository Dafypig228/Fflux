using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
// 🔥 ВАЖНО: Этот using нужен для работы метода AsRandomAccessStream()
using System.Runtime.InteropServices.WindowsRuntime;

namespace FluxCore
{
    public class ScreenService
    {
        private OcrEngine? _ocrEngine;

        public ScreenService()
        {
            try
            {
                _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch { _ocrEngine = null; }
        }

        public async Task<string> AnalyzeScreenAsync()
        {
            if (_ocrEngine == null) return "[OCR недоступен в этой Windows]";

            try
            {
                int width = (int)SystemParameters.PrimaryScreenWidth;
                int height = (int)SystemParameters.PrimaryScreenHeight;

                using (var bitmap = new Bitmap(width, height))
                {
                    using (var g = Graphics.FromImage(bitmap))
                    {
                        g.CopyFromScreen(0, 0, 0, 0, bitmap.Size);
                    }

                    using (var stream = new MemoryStream())
                    {
                        bitmap.Save(stream, ImageFormat.Bmp);
                        stream.Position = 0;

                        // 🔥 ИСПРАВЛЕНИЕ: Явная конвертация потока
                        // Если AsRandomAccessStream подчеркивает красным, убедись, 
                        // что в .csproj стоит target netX.X-windows10.0.19041.0
                        var randomAccessStream = stream.AsRandomAccessStream();

                        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
                        using (var softwareBitmap = await decoder.GetSoftwareBitmapAsync())
                        {
                            var ocrResult = await _ocrEngine.RecognizeAsync(softwareBitmap);

                            StringBuilder sb = new StringBuilder();
                            foreach (var line in ocrResult.Lines) sb.AppendLine(line.Text);

                            var text = sb.ToString().Trim();
                            return string.IsNullOrEmpty(text) ? "[Пустой экран]" : text;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return $"[Ошибка OCR: {ex.Message}]";
            }
        }
    }
}