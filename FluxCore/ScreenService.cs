using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using System.Runtime.InteropServices.WindowsRuntime;

namespace FluxCore
{
    public class ScreenService
    {
        private OcrEngine? _ocrEngine;
        private byte[] _lastScreenHash = Array.Empty<byte>(); // Для Delta-анализа пикселей

        public ScreenService()
        {
            try { _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages(); } catch { }
        }

        public async Task<(string Text, bool VisualChanged)> GetLayer2_OCR_WithDelta()
        {
            if (_ocrEngine == null) return ("[OCR Error]", false);

            int w = (int)SystemParameters.PrimaryScreenWidth;
            int h = (int)SystemParameters.PrimaryScreenHeight;

            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(0, 0, 0, 0, bmp.Size);

            // 1. Delta-анализ (Low-level): Проверяем центр экрана, изменился ли он?
            // Это очень грубая, но супер-быстрая проверка, чтобы не гонять OCR
            bool visualChanged = CheckIfScreenChanged(bmp);

            if (!visualChanged) return ("", false); // Экран статичен, OCR не нужен

            // 2. OCR Выполняем
            using var stream = new MemoryStream();
            bmp.Save(stream, ImageFormat.Bmp);
            stream.Position = 0;
            var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
            using var sBmp = await decoder.GetSoftwareBitmapAsync();
            var res = await _ocrEngine.RecognizeAsync(sBmp);

            // Формируем текст с координатами (простая эмуляция структуры)
            StringBuilder sb = new StringBuilder();
            foreach (var line in res.Lines)
            {
                // Не добавляем мусор, только значимый текст
                if (line.Text.Length > 2)
                    sb.AppendLine(line.Text);
            }

            return (sb.ToString(), true);
        }

        private bool CheckIfScreenChanged(Bitmap bmp)
        {
            // Берем пиксель из центра для теста (в реале нужен хэш всей картинки, но это для скорости)
            var pixel = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
            byte[] currentHash = { (byte)pixel.R, (byte)pixel.G, (byte)pixel.B };

            bool changed = false;
            if (_lastScreenHash.Length == 0 ||
                currentHash[0] != _lastScreenHash[0] ||
                currentHash[1] != _lastScreenHash[1])
            {
                changed = true;
            }

            _lastScreenHash = currentHash;
            return changed;
        }
    }
}