#include <windows.h>
#include <iostream>

// Глобальные переменные
const int HOTKEY_ID = 1;
HWND hWindow;
bool isVisible = false;

// Функция обработки сообщений окна
LRESULT CALLBACK WindowProc(HWND hwnd, UINT uMsg, WPARAM wParam, LPARAM lParam) {
    switch (uMsg) {
    case WM_PAINT:
    {
        PAINTSTRUCT ps;
        HDC hdc = BeginPaint(hwnd, &ps);

        // Заливаем фон темно-серым цветом
        HBRUSH brush = CreateSolidBrush(RGB(30, 30, 30));
        FillRect(hdc, &ps.rcPaint, brush);
        DeleteObject(brush);

        // Рисуем текст
        SetBkMode(hdc, TRANSPARENT);
        SetTextColor(hdc, RGB(255, 255, 255));
        TextOutW(hdc, 20, 20, L"Привет! Я панель на C++", 23);

        EndPaint(hwnd, &ps);
        return 0;
    }
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProc(hwnd, uMsg, wParam, lParam);
}

void ToggleWindow() {
    if (isVisible) {
        ShowWindow(hWindow, SW_HIDE);
        isVisible = false;
    }
    else {
        // Показываем, ставим поверх всех, даем фокус
        ShowWindow(hWindow, SW_SHOW);
        SetForegroundWindow(hWindow);
        SetActiveWindow(hWindow);
        isVisible = true;
    }
}

// Точка входа (аналог main)
int WINAPI wWinMain(HINSTANCE hInstance, HINSTANCE, PWSTR, int nCmdShow) {
    const wchar_t CLASS_NAME[] = L"MyPanelClass";

    WNDCLASS wc = { };
    wc.lpfnWndProc = WindowProc;
    wc.hInstance = hInstance;
    wc.lpszClassName = CLASS_NAME;
    // HREDRAW | VREDRAW нужно для перерисовки при изменении размеров
    wc.style = CS_HREDRAW | CS_VREDRAW;

    RegisterClass(&wc);

    // 1. Создаем окно
    hWindow = CreateWindowEx(
        // WS_EX_TOOLWINDOW = Скрывает из панели задач (и Alt+Tab)
        // WS_EX_LAYERED = Включает поддержку прозрачности
        // WS_EX_TOPMOST = Всегда поверх других окон
        WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TOPMOST,
        CLASS_NAME,
        L"My Panel",
        WS_POPUP, // WS_POPUP = Безрамочное окно
        // Координаты и размер (X, Y, Width, Height)
        (GetSystemMetrics(SM_CXSCREEN) - 600) / 2, 100, 600, 100,
        NULL, NULL, hInstance, NULL
    );

    if (hWindow == NULL) return 0;

    // 2. Настраиваем прозрачность (Alpha = 200 из 255)
    // LWA_ALPHA указывает, что мы используем общую прозрачность
    SetLayeredWindowAttributes(hWindow, 0, 200, LWA_ALPHA);

    // Регистрируем глобальный хоткей Alt + Space
    // MOD_ALT = 0x0001, VK_SPACE = 0x20
    if (!RegisterHotKey(NULL, HOTKEY_ID, MOD_ALT, VK_SPACE)) {
        MessageBox(NULL, L"Не удалось зарегистрировать HotKey!", L"Error", MB_OK);
    }

    // Запускаем цикл сообщений
    MSG msg = { };
    while (GetMessage(&msg, NULL, 0, 0)) {
        // Ловим нажатие HotKey
        if (msg.message == WM_HOTKEY) {
            if (msg.wParam == HOTKEY_ID) {
                ToggleWindow();
            }
        }

        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }

    UnregisterHotKey(NULL, HOTKEY_ID);
    return 0;
}