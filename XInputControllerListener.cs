using System;
using System.Runtime.InteropServices;
using System.Threading;
using Playnite.SDK;
using Playnite.SDK.Events;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Reads XInput controllers directly on a background thread, so controller input reaches the
    /// overlay in Playnite Desktop Mode, in Fullscreen Mode and while a game has the focus.
    /// Playnite raises OnControllerButtonStateChanged only from its Fullscreen app, which is why
    /// 1.0.0 ignored the controller in Desktop Mode.
    ///
    /// Raises ButtonChanged (on the polling thread) with Playnite's ControllerInput values, for presses
    /// and releases, so the rest of the plugin handles both input sources the same way.
    /// Connected pads are polled every ~16 ms; empty slots are re-checked every 2 s (XInputGetState on an
    /// empty slot is slow), and with no pad at all the thread just sleeps.
    /// </summary>
    public sealed class XInputControllerListener : IDisposable
    {
        public event Action<ControllerInput, bool> ButtonChanged;

        private const int MaxControllers = 4;
        private const int PollIntervalMs = 16;
        private const int IdleIntervalMs = 250;
        private const int RescanIntervalMs = 2000;
        private const int ConnectedGraceMs = 3000;

        // Hysteresis so a stick resting near the threshold does not flicker between pressed/released.
        private const short StickPressThreshold = 16000;   // ~49% deflection
        private const short StickReleaseThreshold = 12000;  // ~37% deflection
        private const byte TriggerPressThreshold = 64;
        private const byte TriggerReleaseThreshold = 30;

        private const uint ERROR_SUCCESS = 0;

        [Flags]
        private enum Buttons : ushort
        {
            DPadUp = 0x0001,
            DPadDown = 0x0002,
            DPadLeft = 0x0004,
            DPadRight = 0x0008,
            Start = 0x0010,
            Back = 0x0020,
            LeftThumb = 0x0040,
            RightThumb = 0x0080,
            LeftShoulder = 0x0100,
            RightShoulder = 0x0200,
            Guide = 0x0400, // only reported by the undocumented XInputGetStateEx (ordinal 100)
            A = 0x1000,
            B = 0x2000,
            X = 0x4000,
            Y = 0x8000
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        // XInputGetStateEx (ordinal 100) also reports the Guide button. Fallbacks: xinput1_3, then the
        // documented XInputGetState from xinput9_1_0 (no Guide button).
        [DllImport("xinput1_4.dll", EntryPoint = "#100")]
        private static extern uint XInputGetStateEx14(uint dwUserIndex, out XINPUT_STATE state);

        [DllImport("xinput1_3.dll", EntryPoint = "#100")]
        private static extern uint XInputGetStateEx13(uint dwUserIndex, out XINPUT_STATE state);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState910(uint dwUserIndex, out XINPUT_STATE state);

        private delegate uint GetStateFn(uint index, out XINPUT_STATE state);

        private static readonly (Buttons Flag, ControllerInput Input)[] ButtonMap =
        {
            (Buttons.A, ControllerInput.A),
            (Buttons.B, ControllerInput.B),
            (Buttons.X, ControllerInput.X),
            (Buttons.Y, ControllerInput.Y),
            (Buttons.Start, ControllerInput.Start),
            (Buttons.Back, ControllerInput.Back),
            (Buttons.Guide, ControllerInput.Guide),
            (Buttons.LeftShoulder, ControllerInput.LeftShoulder),
            (Buttons.RightShoulder, ControllerInput.RightShoulder),
            (Buttons.LeftThumb, ControllerInput.LeftStick),
            (Buttons.RightThumb, ControllerInput.RightStick),
            (Buttons.DPadUp, ControllerInput.DPadUp),
            (Buttons.DPadDown, ControllerInput.DPadDown),
            (Buttons.DPadLeft, ControllerInput.DPadLeft),
            (Buttons.DPadRight, ControllerInput.DPadRight),
        };

        // Virtual buttons derived from analog values (sticks, triggers)
        private enum Analog { LsUp, LsDown, LsLeft, LsRight, RsUp, RsDown, RsLeft, RsRight, Lt, Rt, Count }

        private static readonly ControllerInput[] AnalogMap =
        {
            ControllerInput.LeftStickUp, ControllerInput.LeftStickDown, ControllerInput.LeftStickLeft, ControllerInput.LeftStickRight,
            ControllerInput.RightStickUp, ControllerInput.RightStickDown, ControllerInput.RightStickLeft, ControllerInput.RightStickRight,
            ControllerInput.TriggerLeft, ControllerInput.TriggerRight
        };

        private class PadState
        {
            public bool Connected;
            public uint LastPacket;
            public ushort Buttons;
            public readonly bool[] AnalogDown = new bool[(int)Analog.Count];
            public int NextProbeTick;
        }

        private readonly ILogger logger;
        private readonly PadState[] pads = new PadState[MaxControllers];
        private readonly ManualResetEvent stopEvent = new ManualResetEvent(false);
        private Thread thread;
        private GetStateFn getState;
        private volatile int lastConnectedTick;
        private volatile bool anyConnected;

        public XInputControllerListener(ILogger logger)
        {
            this.logger = logger;
            for (int i = 0; i < MaxControllers; i++) pads[i] = new PadState();
        }

        /// <summary>True while at least one XInput controller is (or was within the last few seconds) connected.</summary>
        public bool HasActiveController =>
            anyConnected || (lastConnectedTick != 0 && unchecked(Environment.TickCount - lastConnectedTick) < ConnectedGraceMs);

        public bool IsRunning => thread != null;

        public bool Start()
        {
            if (thread != null) return true;
            getState = ResolveGetState();
            if (getState == null)
            {
                logger.Warn("[XInput] No XInput library available; controller input relies on Playnite Fullscreen only.");
                return false;
            }
            stopEvent.Reset();
            thread = new Thread(Run) { IsBackground = true, Name = "PlayniteNextOverlay XInput", Priority = ThreadPriority.AboveNormal };
            thread.Start();
            return true;
        }

        public void Stop()
        {
            var t = thread;
            if (t == null) return;
            stopEvent.Set();
            try { t.Join(500); } catch { }
            thread = null;
            anyConnected = false;
        }

        public void Dispose()
        {
            Stop();
        }

        private GetStateFn ResolveGetState()
        {
            var candidates = new (string Name, GetStateFn Fn)[]
            {
                ("xinput1_4 (Ex)", XInputGetStateEx14),
                ("xinput1_3 (Ex)", XInputGetStateEx13),
                ("xinput9_1_0", XInputGetState910),
            };
            foreach (var c in candidates)
            {
                try
                {
                    XINPUT_STATE s;
                    c.Fn(0, out s); // throws if the dll / entry point is missing
                    logger.Info($"[XInput] Using {c.Name} for controller input.");
                    return c.Fn;
                }
                catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
                {
                }
            }
            return null;
        }

        private void Run()
        {
            try
            {
                while (true)
                {
                    int now = Environment.TickCount;
                    bool connectedNow = false;

                    for (uint i = 0; i < MaxControllers; i++)
                    {
                        var pad = pads[i];
                        if (!pad.Connected && unchecked(now - pad.NextProbeTick) < 0)
                            continue; // empty slot, probe again later

                        XINPUT_STATE state;
                        uint result = getState(i, out state);
                        if (result != ERROR_SUCCESS)
                        {
                            if (pad.Connected)
                            {
                                logger.Info($"[XInput] Controller {i} disconnected.");
                                ReleaseAll(pad);
                                pad.Connected = false;
                            }
                            pad.NextProbeTick = unchecked(now + RescanIntervalMs);
                            continue;
                        }

                        if (!pad.Connected)
                        {
                            logger.Info($"[XInput] Controller {i} connected.");
                            pad.Connected = true;
                            pad.LastPacket = state.dwPacketNumber;
                            // Take the current state as baseline: buttons already held when the pad appears
                            // (or when Playnite starts) do not fire.
                            pad.Buttons = state.Gamepad.wButtons;
                            UpdateAnalog(pad, state.Gamepad, emit: false);
                        }
                        else if (state.dwPacketNumber != pad.LastPacket)
                        {
                            pad.LastPacket = state.dwPacketNumber;
                            Process(pad, state.Gamepad);
                        }

                        connectedNow = true;
                    }

                    anyConnected = connectedNow;
                    if (connectedNow) lastConnectedTick = Environment.TickCount | 1;

                    if (stopEvent.WaitOne(connectedNow ? PollIntervalMs : IdleIntervalMs))
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.Error($"[XInput] Polling thread stopped: {ex}");
                anyConnected = false;
            }
        }

        private void Process(PadState pad, XINPUT_GAMEPAD gp)
        {
            ushort changed = (ushort)(pad.Buttons ^ gp.wButtons);
            pad.Buttons = gp.wButtons;
            if (changed != 0)
            {
                foreach (var m in ButtonMap)
                {
                    if ((changed & (ushort)m.Flag) != 0)
                        Emit(m.Input, (gp.wButtons & (ushort)m.Flag) != 0);
                }
            }
            UpdateAnalog(pad, gp, emit: true);
        }

        private void UpdateAnalog(PadState pad, XINPUT_GAMEPAD gp, bool emit)
        {
            SetAnalog(pad, Analog.LsUp, StickDown(pad, Analog.LsUp, gp.sThumbLY), emit);
            SetAnalog(pad, Analog.LsDown, StickDown(pad, Analog.LsDown, Neg(gp.sThumbLY)), emit);
            SetAnalog(pad, Analog.LsRight, StickDown(pad, Analog.LsRight, gp.sThumbLX), emit);
            SetAnalog(pad, Analog.LsLeft, StickDown(pad, Analog.LsLeft, Neg(gp.sThumbLX)), emit);
            SetAnalog(pad, Analog.RsUp, StickDown(pad, Analog.RsUp, gp.sThumbRY), emit);
            SetAnalog(pad, Analog.RsDown, StickDown(pad, Analog.RsDown, Neg(gp.sThumbRY)), emit);
            SetAnalog(pad, Analog.RsRight, StickDown(pad, Analog.RsRight, gp.sThumbRX), emit);
            SetAnalog(pad, Analog.RsLeft, StickDown(pad, Analog.RsLeft, Neg(gp.sThumbRX)), emit);
            SetAnalog(pad, Analog.Lt, TriggerDown(pad, Analog.Lt, gp.bLeftTrigger), emit);
            SetAnalog(pad, Analog.Rt, TriggerDown(pad, Analog.Rt, gp.bRightTrigger), emit);
        }

        private static int Neg(short v) => -(int)v;

        private static bool StickDown(PadState pad, Analog a, int value) =>
            pad.AnalogDown[(int)a] ? value > StickReleaseThreshold : value > StickPressThreshold;

        private static bool TriggerDown(PadState pad, Analog a, byte value) =>
            pad.AnalogDown[(int)a] ? value > TriggerReleaseThreshold : value > TriggerPressThreshold;

        private void SetAnalog(PadState pad, Analog a, bool down, bool emit)
        {
            if (pad.AnalogDown[(int)a] == down) return;
            pad.AnalogDown[(int)a] = down;
            if (emit) Emit(AnalogMap[(int)a], down);
        }

        private void ReleaseAll(PadState pad)
        {
            foreach (var m in ButtonMap)
            {
                if ((pad.Buttons & (ushort)m.Flag) != 0)
                    Emit(m.Input, false);
            }
            pad.Buttons = 0;
            for (int a = 0; a < (int)Analog.Count; a++)
            {
                if (pad.AnalogDown[a])
                {
                    pad.AnalogDown[a] = false;
                    Emit(AnalogMap[a], false);
                }
            }
        }

        private void Emit(ControllerInput input, bool pressed)
        {
            try
            {
                ButtonChanged?.Invoke(input, pressed);
            }
            catch (Exception ex)
            {
                logger.Error($"[XInput] Error handling {input} {(pressed ? "press" : "release")}: {ex}");
            }
        }
    }
}
