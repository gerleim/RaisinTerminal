using System.Text;

namespace RaisinTerminal.Core.Terminal;

/// <summary>
/// ANSI/VT100 escape sequence parser. Processes a byte stream and emits
/// parsed actions (print, execute, CSI dispatch, etc.) to update the terminal buffer.
/// Handles UTF-8 multi-byte sequences so that characters like box-drawing (─│╭╰)
/// and emoji are decoded correctly before being emitted via Print.
/// </summary>
public class AnsiParser
{
    private enum State { Ground, Escape, EscapeIntermediate, CsiEntry, CsiParam, CsiIntermediate, OscString }

    private State _state = State.Ground;
    private readonly List<int> _params = [];
    private int _currentParam;
    private readonly List<byte> _intermediates = [];
    private readonly List<byte> _oscData = [];
    private byte _privateMarker;

    // UTF-8 accumulator
    private readonly byte[] _utf8Buf = new byte[4];
    private int _utf8Len;      // expected total bytes for current character
    private int _utf8Received; // bytes collected so far

    public event Action<char>? Print;
    public event Action<byte>? Execute;
    /// <summary>
    /// Fired for CSI sequences. Args: final char, params, intermediates, private marker (0 if none).
    /// Private marker is '?' for DEC private modes, '>' for secondary DA, etc.
    /// </summary>
    /// <summary>
    /// Fired for single-character escape sequences (ESC followed by a final byte 0x30–0x7E).
    /// E.g. ESC 7 (DECSC), ESC 8 (DECRC), ESC M (RI), ESC D (IND), ESC E (NEL).
    /// </summary>
    public event Action<char>? EscDispatch;
    public event Action<char, int[], byte[], byte>? CsiDispatch;
    public event Action<string>? OscDispatch;

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            ProcessByte(b);
    }

    private void ProcessByte(byte b)
    {
        // If we're accumulating a UTF-8 multi-byte sequence in Ground state,
        // continuation bytes (10xxxxxx) feed into the accumulator.
        if (_utf8Len > 0)
        {
            if ((b & 0xC0) == 0x80) // valid continuation byte
            {
                _utf8Buf[_utf8Received++] = b;
                if (_utf8Received == _utf8Len)
                {
                    // Decode and emit
                    var str = Encoding.UTF8.GetString(_utf8Buf, 0, _utf8Len);
                    _utf8Len = 0;
                    foreach (var ch in str)
                        Print?.Invoke(ch);
                }
                return;
            }
            // Invalid continuation — discard the partial sequence and re-process this byte
            _utf8Len = 0;
        }

        switch (_state)
        {
            case State.Ground:
                if (b == 0x1B)
                {
                    _state = State.Escape;
                }
                else if (b < 0x20)
                {
                    Execute?.Invoke(b);
                }
                else if (b < 0x80)
                {
                    // ASCII printable — emit directly
                    Print?.Invoke((char)b);
                }
                else
                {
                    // Start of UTF-8 multi-byte sequence
                    if ((b & 0xE0) == 0xC0)      _utf8Len = 2; // 110xxxxx → 2 bytes
                    else if ((b & 0xF0) == 0xE0)  _utf8Len = 3; // 1110xxxx → 3 bytes
                    else if ((b & 0xF8) == 0xF0)  _utf8Len = 4; // 11110xxx → 4 bytes
                    else break; // invalid lead byte, ignore

                    _utf8Buf[0] = b;
                    _utf8Received = 1;
                }
                break;

            case State.Escape:
                if (b == '[')
                {
                    _state = State.CsiEntry;
                    _params.Clear();
                    _currentParam = 0;
                    _intermediates.Clear();
                    _privateMarker = 0;
                }
                else if (b == ']')
                {
                    _state = State.OscString;
                    _oscData.Clear();
                }
                else if (b >= 0x20 && b <= 0x2F)
                {
                    _intermediates.Add(b);
                    _state = State.EscapeIntermediate;
                }
                else if (b >= 0x30 && b <= 0x7E)
                {
                    // Single-character escape sequence (e.g. ESC 7, ESC 8, ESC M, ESC D)
                    EscDispatch?.Invoke((char)b);
                    _state = State.Ground;
                }
                else
                {
                    _state = State.Ground;
                }
                break;

            case State.EscapeIntermediate:
                if (b >= 0x30 && b <= 0x7E)
                    _state = State.Ground;
                else if (b >= 0x20 && b <= 0x2F)
                    _intermediates.Add(b);
                break;

            case State.CsiEntry:
            case State.CsiParam:
                if (b >= 0x30 && b <= 0x39) // digit
                {
                    _currentParam = _currentParam * 10 + (b - 0x30);
                    _state = State.CsiParam;
                }
                else if (b == ';') // param separator
                {
                    _params.Add(_currentParam);
                    _currentParam = 0;
                    _state = State.CsiParam;
                }
                else if (b >= 0x3C && b <= 0x3F && _state == State.CsiEntry) // private marker: < = > ?
                {
                    _privateMarker = b;
                    _state = State.CsiParam;
                }
                else if (b >= 0x20 && b <= 0x2F) // intermediate
                {
                    _params.Add(_currentParam);
                    _intermediates.Add(b);
                    _state = State.CsiIntermediate;
                }
                else if (b >= 0x40 && b <= 0x7E) // final
                {
                    _params.Add(_currentParam);
                    CsiDispatch?.Invoke((char)b, _params.ToArray(), _intermediates.ToArray(), _privateMarker);
                    _state = State.Ground;
                }
                else
                {
                    _state = State.Ground;
                }
                break;

            case State.CsiIntermediate:
                if (b >= 0x40 && b <= 0x7E)
                {
                    CsiDispatch?.Invoke((char)b, _params.ToArray(), _intermediates.ToArray(), _privateMarker);
                    _state = State.Ground;
                }
                else if (b >= 0x20 && b <= 0x2F)
                    _intermediates.Add(b);
                else
                    _state = State.Ground;
                break;

            case State.OscString:
                if (b == 0x07 || b == 0x1B)
                {
                    OscDispatch?.Invoke(Encoding.UTF8.GetString(_oscData.ToArray()));
                    _state = b == 0x1B ? State.Escape : State.Ground;
                }
                else
                {
                    _oscData.Add(b);
                }
                break;
        }
    }
}
