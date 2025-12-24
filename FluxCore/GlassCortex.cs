using System;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FluxCore
{
    public static class GlassCortex
    {
        public static BitmapSource CaptureBackground(Window window)
        {
            try
            {
                // Скрываем окно на мгновение, чтобы снять фон (или используем Win11 API)
                window.Opacity = 0;

                int left = (int)window.Left;
                int top = (int)window.Top;
                int width = (int)window.ActualWidth;
                int height = (int)window.ActualHeight;

                if (width <= 0 || height <= 0) return null;

                using (Bitmap bmp = new Bitmap(width, height))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(left, top, 0, 0, bmp.Size);
                    }

                    var hBitmap = bmp.GetHbitmap();
                    var source = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());

                    window.Opacity = 1;
                    return source;
                }
            }
            catch { return null; }
        }
    }
}