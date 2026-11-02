// Reused from HB9IIU Experimental H.266 Player (GPL v3); original source credits in docs/WINDOWS_MONITOR.md.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DatvRx.Player
{
    // The sound in Windows (its line in the Windows volume mixer), on the default speakers. In the H.266
    // test player the sound is ffplay's: ProcessId is ffplay's process (0 = none yet).
    // Volume and mute are set here instead of in VLC: the Windows meter measures before them,
    // so the VU meter keeps showing the received sound even when muted. Use on the UI thread only.
    public sealed class AudioSession
    {
        private static readonly TimeSpan SearchEvery = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan SearchSoon = TimeSpan.FromMilliseconds(250);   // ffplay's line not there yet

        private int _processId;
        private List<object> _sessions = new List<object>();
        private DateTime _nextSearch = DateTime.MinValue;
        private int _volume = -1;
        private bool _muted;

        // the process whose sound this is; a new one is searched for at once
        public int ProcessId
        {
            get { return _processId; }
            set
            {
                if (value == _processId)
                    return;
                _processId = value;
                _sessions = new List<object>();
                _nextSearch = DateTime.MinValue;
                _volume = -1;
            }
        }

        // true while Windows has our sound (VLC opens it when the sound starts)
        public bool Found { get { return _sessions.Count > 0; } }

        // call every 50 ms with the wanted sound; returns the peak of the last moment, 0..1
        public float Update(int volume, bool muted)
        {
            // searched again now and then: VLC may open a new one (other speakers chosen in Windows)
            if (_processId != 0 && DateTime.UtcNow >= _nextSearch)
            {
                _sessions = FindOurSessions();
                _nextSearch = DateTime.UtcNow + (_sessions.Count > 0 ? SearchEvery : SearchSoon);
                _volume = -1;
            }
            if (volume != _volume || muted != _muted)
            {
                _volume = volume;
                _muted = muted;
                // same feel as VLC's own volume (DirectSound: 60 dB over the range = cube)
                float level = (float)Math.Pow(volume / 100.0, 3);
                Guid context = Guid.Empty;
                foreach (object session in _sessions)
                {
                    try
                    {
                        var simple = (ISimpleAudioVolume)session;
                        simple.SetMasterVolume(level, ref context);
                        simple.SetMute(muted, ref context);
                    }
                    catch (Exception) { }
                }
            }

            float peak = 0f;
            foreach (object session in _sessions)
            {
                try
                {
                    if (((IAudioMeterInformation)session).GetPeakValue(out float value) == 0)
                        peak = Math.Max(peak, value);
                }
                catch (Exception) { }
            }
            return peak;
        }

        private List<object> FindOurSessions()
        {
            var found = new List<object>();
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out IMMDevice device));
                Guid managerId = typeof(IAudioSessionManager2).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref managerId, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out object managerObject));
                var manager = (IAudioSessionManager2)managerObject;
                Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out IAudioSessionEnumerator sessions));
                Marshal.ThrowExceptionForHR(sessions.GetCount(out int count));
                for (int i = 0; i < count; i++)
                {
                    Marshal.ThrowExceptionForHR(sessions.GetSession(i, out object session));
                    if (session is IAudioSessionControl2 control &&
                        control.GetProcessId(out uint processId) == 0 && processId == _processId)
                        found.Add(session);
                }
            }
            catch (Exception)
            {
                // no speakers: nothing to show or set
            }
            return found;
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints();
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            int GetAudioSessionControl();
            int GetSimpleAudioVolume();
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // IAudioSessionControl (9 methods, not used)
            int GetState(); int GetDisplayName(); int SetDisplayName(); int GetIconPath(); int SetIconPath();
            int GetGroupingParam(); int SetGroupingParam(); int RegisterAudioSessionNotification(); int UnregisterAudioSessionNotification();
            int GetSessionIdentifier();
            int GetSessionInstanceIdentifier();
            [PreserveSig] int GetProcessId(out uint processId);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute(bool mute, ref Guid eventContext);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            [PreserveSig] int GetPeakValue(out float peak);
        }
    }
}

