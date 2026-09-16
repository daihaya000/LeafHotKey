namespace LeafHotKey;

/// <summary>送信できなかったトークンを含む結果。</summary>
public sealed class SendResult
{
    public required int SentEvents { get; init; }

    /// <summary>現在の配列などで解決できず、送信しなかったトークン。</summary>
    public required IReadOnlyList<string> Unresolved { get; init; }

    public bool Success => Unresolved.Count == 0;
}

/// <summary>
/// SendToken 列を SendInput へ変換して送る。
/// 1 つの送信単位は 1 回の SendInput で送り、途中に他の入力が割り込まないようにする。
/// 自分が送ったイベントは ExtraInfo の署名で識別でき、フック側で自己入力を無視できる。
/// </summary>
public sealed class KeySender : IKeySink
{
    /// <summary>LeafHotKey が注入したイベントであることを示す署名。</summary>
    public const ulong DefaultSignature = 0x4C48_4B45_5901;

    private readonly UIntPtr _signature;

    public KeySender(ulong signature = DefaultSignature)
    {
        _signature = new UIntPtr(signature);
    }

    public UIntPtr Signature => _signature;

    public SendResult Send(IReadOnlyList<SendToken> tokens) => Send(tokens, null);

    public SendResult Send(IReadOnlyList<SendToken> tokens, IntPtr? layout)
    {
        var activeLayout = layout ?? KeyResolver.CurrentLayout;
        var inputs = new List<NativeMethods.Input>(tokens.Count * 6);
        var unresolved = new List<string>();

        foreach (var token in tokens)
        {
            if (!KeyResolver.TryResolve(token, activeLayout, out var resolved))
            {
                unresolved.Add(token.ToString());
                continue;
            }

            AppendToken(inputs, resolved);
        }

        var sent = 0;
        if (inputs.Count > 0)
        {
            var buffer = inputs.ToArray();
            sent = (int)NativeMethods.SendInput(
                (uint)buffer.Length,
                buffer,
                System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Input>());
        }

        return new SendResult { SentEvents = sent, Unresolved = unresolved };
    }

    private void AppendToken(List<NativeMethods.Input> inputs, ResolvedKey resolved)
    {
        // 修飾キーは押す順と離す順を対称にする。
        var modifiers = new[] { SendModifiers.Ctrl, SendModifiers.Shift, SendModifiers.Alt, SendModifiers.Win }
            .Where(modifier => resolved.Modifiers.HasFlag(modifier))
            .ToArray();

        if (resolved.Action != KeyAction.Up)
        {
            foreach (var modifier in modifiers)
            {
                inputs.Add(CreateInput(KeyResolver.VirtualKeyFor(modifier), keyUp: false));
            }
        }

        switch (resolved.Action)
        {
            case KeyAction.Press:
                inputs.Add(CreateInput(resolved.VirtualKey, keyUp: false));
                inputs.Add(CreateInput(resolved.VirtualKey, keyUp: true));
                break;
            case KeyAction.Down:
                inputs.Add(CreateInput(resolved.VirtualKey, keyUp: false));
                break;
            case KeyAction.Up:
                inputs.Add(CreateInput(resolved.VirtualKey, keyUp: true));
                break;
        }

        if (resolved.Action == KeyAction.Press)
        {
            foreach (var modifier in modifiers.Reverse())
            {
                inputs.Add(CreateInput(KeyResolver.VirtualKeyFor(modifier), keyUp: true));
            }
        }
    }

    private NativeMethods.Input CreateInput(ushort virtualKey, bool keyUp)
    {
        var flags = keyUp ? NativeMethods.KeyEventKeyUp : 0u;
        if (NativeMethods.IsExtendedKey(virtualKey)) flags |= NativeMethods.KeyEventExtendedKey;

        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Union = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = virtualKey,
                    ScanCode = 0,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = _signature,
                },
            },
        };
    }
}
