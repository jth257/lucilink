using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using LuciLink.Core.Control;

namespace LuciLink.Client;

public class InputManager
{
    private readonly ControlSender _sender;
    private readonly Image _imageControl;
    private int _videoWidth;
    private int _videoHeight;
    private readonly Window _window;

    // Android MotionEvent 상수
    private const int ACTION_DOWN = 0;
    private const int ACTION_UP = 1;
    private const int ACTION_MOVE = 2;
    
    // 손가락 터치로 시뮬레이션 (pointerId=0 = 첫 번째 손가락)
    private const long POINTER_ID_FINGER = 0;

    // 드래그 쓰로틀링
    private DateTime _lastMoveTime = DateTime.MinValue;
    private const int MOVE_INTERVAL_MS = 10;
    private bool _isSendingMove = false;


    public InputManager(ControlSender sender, Image imageControl, Window window)
    {
        _sender = sender;
        _imageControl = imageControl;
        _window = window;
        
        _imageControl.MouseDown += OnMouseDown;
        _imageControl.MouseUp += OnMouseUp;
        _imageControl.MouseMove += OnMouseMove;
        
        _window.PreviewKeyDown += OnKeyDown;
        _window.PreviewKeyUp += OnKeyUp;

        // 한글 등 IME 조합 문자 입력 처리
        TextCompositionManager.AddTextInputHandler(_window, OnTextInput);
        TextCompositionManager.AddTextInputStartHandler(_window, OnTextInputStart);
    }

    public void UpdateVideoSize(int width, int height)
    {
        _videoWidth = width;
        _videoHeight = height;
    }

    public void Detach()
    {
        _imageControl.MouseDown -= OnMouseDown;
        _imageControl.MouseUp -= OnMouseUp;
        _imageControl.MouseMove -= OnMouseMove;
        _window.PreviewKeyDown -= OnKeyDown;
        _window.PreviewKeyUp -= OnKeyUp;
        TextCompositionManager.RemoveTextInputHandler(_window, OnTextInput);
        TextCompositionManager.RemoveTextInputStartHandler(_window, OnTextInputStart);
    }

    // ===== 마우스 → 터치 =====

    private async void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 오른쪽 클릭 = Android 뒤로가기
        if (e.ChangedButton == MouseButton.Right)
        {
            await _sender.InjectKeyAsync(0, 4, 0);
            await _sender.InjectKeyAsync(1, 4, 0);
            e.Handled = true;
            return;
        }

        _imageControl.CaptureMouse();
        
        var (x, y) = MapToAndroid(e);
        if (x < 0) return;

        await _sender.InjectTouchAsync(ACTION_DOWN, POINTER_ID_FINGER, x, y, 
            _videoWidth, _videoHeight, 1.0f, 0, 0);
    }

    private async void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right) return;

        _imageControl.ReleaseMouseCapture();
        
        var (x, y) = MapToAndroid(e);
        if (x < 0) return;

        await _sender.InjectTouchAsync(ACTION_UP, POINTER_ID_FINGER, x, y,
            _videoWidth, _videoHeight, 0f, 0, 0);
    }

    private async void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_isSendingMove) return;

        var now = DateTime.UtcNow;
        if ((now - _lastMoveTime).TotalMilliseconds < MOVE_INTERVAL_MS) return;

        var (x, y) = MapToAndroid(e);
        if (x < 0) return;

        _isSendingMove = true;
        _lastMoveTime = now;
        try
        {
            await _sender.InjectTouchAsync(ACTION_MOVE, POINTER_ID_FINGER, x, y,
                _videoWidth, _videoHeight, 1.0f, 0, 0);
        }
        finally
        {
            _isSendingMove = false;
        }
    }

    /// <summary>
    /// WPF Image 좌표 → Android 화면 좌표 변환 (Uniform stretch 보정)
    /// </summary>
    private (int x, int y) MapToAndroid(MouseEventArgs e)
    {
        if (!_sender.IsConnected || _videoWidth == 0 || _videoHeight == 0) 
            return (-1, -1);

        var pos = e.GetPosition(_imageControl);
        double imgW = _imageControl.ActualWidth;
        double imgH = _imageControl.ActualHeight;
        if (imgW <= 0 || imgH <= 0) return (-1, -1);

        double scale = Math.Min(imgW / _videoWidth, imgH / _videoHeight);
        double renderW = _videoWidth * scale;
        double renderH = _videoHeight * scale;
        double offsetX = (imgW - renderW) / 2;
        double offsetY = (imgH - renderH) / 2;

        double relX = pos.X - offsetX;
        double relY = pos.Y - offsetY;

        if (relX < 0 || relX > renderW || relY < 0 || relY > renderH)
            return (-1, -1);

        int ax = Math.Clamp((int)(relX / scale), 0, _videoWidth - 1);
        int ay = Math.Clamp((int)(relY / scale), 0, _videoHeight - 1);
        return (ax, ay);
    }

    // ===== 한글 IME 텍스트 입력 =====

    private void OnTextInputStart(object sender, TextCompositionEventArgs e)
    {
        // IME 조합 시작 (필요시 추가 로직)
    }

    private async void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!_sender.IsConnected) return;
        if (string.IsNullOrEmpty(e.Text)) return;

        // 일반 ASCII (영문, 숫자, 특수문자)는 키코드로 처리되므로 스킵
        // 한글, 일본어, 중국어 등 비-ASCII 문자만 텍스트 주입으로 처리
        char ch = e.Text[0];
        if (ch < 128)
        {
            // ASCII 문자는 KeyDown/KeyUp에서 처리
            return;
        }

        // 한글 등 IME 조합 문자 → scrcpy SET_CLIPBOARD + PASTE로 주입
        await _sender.InjectTextAsync(e.Text);
        e.Handled = true;
    }

    // ===== 키보드 =====

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_sender.IsConnected) return;

        // IME에서 이미 처리된 키는 무시
        if (e.Key == Key.ImeProcessed) return;

        int keyCode = MapKeyToAndroid(e.Key);
        if (keyCode != 0)
        {
            await _sender.InjectKeyAsync(0, keyCode, 0);
            e.Handled = true;
        }
    }

    private async void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (!_sender.IsConnected) return;
        if (e.Key == Key.ImeProcessed) return;

        int keyCode = MapKeyToAndroid(e.Key);
        if (keyCode != 0)
        {
            await _sender.InjectKeyAsync(1, keyCode, 0);
            e.Handled = true;
        }
    }
    
    private static int MapKeyToAndroid(Key key) => key switch
    {
        // 네비게이션
        Key.Home => 3,          // KEYCODE_HOME
        Key.Escape => 4,        // KEYCODE_BACK
        Key.BrowserBack => 4,   // KEYCODE_BACK
        Key.Back => 67,         // KEYCODE_DEL (Backspace)
        Key.Delete => 112,      // KEYCODE_FORWARD_DEL
        Key.Enter => 66,        // KEYCODE_ENTER
        Key.Space => 62,        // KEYCODE_SPACE
        Key.Tab => 61,          // KEYCODE_TAB
        
        // 방향키
        Key.Up => 19,
        Key.Down => 20,
        Key.Left => 21,
        Key.Right => 22,
        
        // 볼륨
        Key.VolumeUp => 24,
        Key.VolumeDown => 25,
        
        // 숫자 0-9
        Key.D0 => 7, Key.D1 => 8, Key.D2 => 9, Key.D3 => 10, Key.D4 => 11,
        Key.D5 => 12, Key.D6 => 13, Key.D7 => 14, Key.D8 => 15, Key.D9 => 16,
        
        // A-Z
        Key.A => 29, Key.B => 30, Key.C => 31, Key.D => 32, Key.E => 33,
        Key.F => 34, Key.G => 35, Key.H => 36, Key.I => 37, Key.J => 38,
        Key.K => 39, Key.L => 40, Key.M => 41, Key.N => 42, Key.O => 43,
        Key.P => 44, Key.Q => 45, Key.R => 46, Key.S => 47, Key.T => 48,
        Key.U => 49, Key.V => 50, Key.W => 51, Key.X => 52, Key.Y => 53, Key.Z => 54,

        // 특수문자
        Key.OemPeriod => 56,    // KEYCODE_PERIOD
        Key.OemComma => 55,     // KEYCODE_COMMA
        Key.OemMinus => 69,     // KEYCODE_MINUS
        Key.OemPlus => 70,      // KEYCODE_EQUALS (=/+)
        Key.Oem1 => 74,         // KEYCODE_SEMICOLON (;)
        Key.Oem2 => 76,         // KEYCODE_SLASH (/)
        Key.Oem4 => 71,         // KEYCODE_LEFT_BRACKET ([)
        Key.Oem6 => 72,         // KEYCODE_RIGHT_BRACKET (])
        Key.Oem5 => 73,         // KEYCODE_BACKSLASH (\)
        Key.Oem7 => 75,         // KEYCODE_APOSTROPHE (')
        Key.OemTilde => 68,     // KEYCODE_GRAVE (`)
        
        _ => 0
    };
}
