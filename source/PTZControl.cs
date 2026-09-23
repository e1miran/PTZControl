// PTZControl.cs
//
// Controls a DirectShow/UVC camera's Pan/Tilt/Zoom (and Roll/Exposure/Iris/
// Focus) via IAMCameraControl — the same properties behind the "Camera
// Controls" tab in the camera's driver properties dialog.
//
// TWO MODES:
//
//   1) One-off command line mode:
//        PTZControl.exe list
//        PTZControl.exe status [deviceIndex]
//        PTZControl.exe set   <pan|tilt|zoom|roll|exposure|iris|focus> <value> [deviceIndex]
//        PTZControl.exe move  <pan|tilt|zoom|roll|exposure|iris|focus> <delta> [deviceIndex]
//        PTZControl.exe reset <pan|tilt|zoom|roll|exposure|iris|focus>         [deviceIndex]
//        PTZControl.exe presetsave <1-8> [deviceIndex]
//        PTZControl.exe presetload <1-8> [deviceIndex]
//        PTZControl.exe presets   (lists saved presets)
//        PTZControl.exe config    (shows the saved device index / step sizes)
//        PTZControl.exe setsteps <deviceIndex> <panStep> <tiltStep> <zoomStep>
//
//   2) Background hotkey daemon:
//        PTZControl.exe hotkeys [deviceIndex] [panStep] [tiltStep] [zoomStep]
//        (or just run PTZControl.exe with no arguments at all)
//
//      Registers global hotkeys, sits in the system tray, and reacts to:
//        Ctrl+Alt+Left/Right         pan
//        Ctrl+Alt+Up/Down            tilt
//        Ctrl+Alt+PageUp/PageDown    zoom
//        Ctrl+Alt+Home               reset pan/tilt/zoom to defaults
//        Ctrl+Alt+End                balloon tip with current status
//        F1 - F8                     recall camera-scene preset 1-8 (no notification)
//        Ctrl+Alt+Shift+F1 - F8      save current pan/tilt/zoom as preset 1-8
//      Right-click the tray icon to Exit.
//      If pan or tilt currently has zero range (stuck at min/max zoom crop),
//      the corresponding hotkey simply does nothing - no automatic zoom
//      adjustment happens. Use 'status' to see each property's live range.
//
//      NOTE: F1-F8 are registered as plain (unmodified) global hotkeys for
//      recall, per the "camera scene on a function key" request. If those
//      keys are already used for something else on your PC (media controls,
//      another app's global hotkeys, a laptop's Fn-lock behavior), those
//      presses may not reach whichever app you meant to control instead.
//      If that happens, change VK_F1..VK_F8 registration below to use a
//      modifier (e.g. add MOD_ALT) instead of 0.
//
//      Any deviceIndex/panStep/tiltStep/zoomStep given on the command line
//      (to 'hotkeys' or 'setsteps') is saved and becomes the default the next
//      time you launch with no arguments - so you only need to specify your
//      preferred step sizes once. Presets and this config are saved to:
//        %APPDATA%\PTZControl\presets.ini
//        %APPDATA%\PTZControl\config.ini
//      so they persist across restarts of this program and reboots.
//
// Compile (csc ships with .NET Framework — no extra install needed):
//   csc.exe /target:winexe /out:PTZControl.exe /reference:System.Windows.Forms.dll,System.Drawing.dll PTZControl.cs
//
// /target:winexe builds this as a Windows GUI-subsystem app so double-clicking
// PTZControl.exe (or launching 'hotkeys' mode) never pops up a console window.
// The CLI commands (list/status/set/move/etc.) still print normally when run
// from an existing Command Prompt window, via AttachConsole in Main().
//
// To give the exe (and its tray icon) a custom icon instead of the generic
// Windows default, put an .ico file next to this .cs file and add
// /win32icon:<yourfile>.ico to the compile command, e.g.:
//   csc.exe /target:winexe /win32icon:PTZControl.ico /out:PTZControl.exe /reference:System.Windows.Forms.dll,System.Drawing.dll PTZControl.cs
// That single flag sets the icon shown in File Explorer, the taskbar, and any
// shortcut you make to the exe. The tray icon automatically picks up the same
// icon at runtime (see SetupTrayIcon below) - no separate step needed.
//
// To have it start automatically with Windows, put a shortcut to
// "PTZControl.exe hotkeys" in your Startup folder (Win+R -> shell:startup).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;

namespace PTZControl
{
    #region COM interop declarations (DirectShow)

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator(ref Guid pType, out IEnumMoniker ppEnumMoniker, int dwFlags);
    }

    [ComImport, Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86")]
    internal class SystemDeviceEnum { }

    // IPropertyBag inherits from IUnknown only (NOT IDispatch) - it is a
    // plain COM interface, not a dual interface. Declaring it as
    // InterfaceIsDual (as an earlier version of this file did) makes .NET
    // reserve 4 extra vtable slots for IDispatch's methods that don't
    // actually exist on this interface, so every call to Read()/Write() ends
    // up invoking whatever real method happens to sit 4 slots further down
    // the object's actual vtable - a wrong-method call with a mismatched
    // argument list, which corrupts the stack. This is what was causing the
    // STATUS_STACK_BUFFER_OVERRUN (0xc0000409) crash in ucrtbase.dll: it only
    // shows up as a visible crash depending on what code happens to occupy
    // that wrong slot for a given camera driver, which is why it appeared
    // "random" across different machines.
    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyBag
    {
        [PreserveSig]
        int Read([MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
                  [MarshalAs(UnmanagedType.Struct)] ref object pVar,
                  IntPtr pErrorLog);

        [PreserveSig]
        int Write([MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
                   [MarshalAs(UnmanagedType.Struct)] ref object pVar);
    }

    [ComImport, Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IBaseFilterMarker { }

    [ComImport, Guid("C6E13370-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAMCameraControl
    {
        [PreserveSig]
        int GetRange(CameraControlProperty Property, out int pMin, out int pMax,
                      out int pSteppingDelta, out int pDefault, out CameraControlFlags pCapsFlags);

        [PreserveSig]
        int Set(CameraControlProperty Property, int lValue, CameraControlFlags Flags);

        [PreserveSig]
        int Get(CameraControlProperty Property, out int lValue, out CameraControlFlags pFlags);
    }

    internal enum CameraControlProperty
    {
        Pan = 0,
        Tilt = 1,
        Roll = 2,
        Zoom = 3,
        Exposure = 4,
        Iris = 5,
        Focus = 6
    }

    [Flags]
    internal enum CameraControlFlags
    {
        Auto = 0x0001,
        Manual = 0x0002
    }

    #endregion

    internal class DeviceInfo
    {
        public IMoniker Moniker;
        public string Name;
    }

    internal struct Preset
    {
        public int Pan, Tilt, Zoom;
    }

    #region Preset persistence

    internal static class PresetStore
    {
        static string FilePath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PTZControl");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "presets.ini");
        }

        public static Dictionary<int, Preset> Load()
        {
            var result = new Dictionary<int, Preset>();
            string path = FilePath();
            if (!File.Exists(path))
                return result;

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;

                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (!key.StartsWith("F", StringComparison.OrdinalIgnoreCase))
                    continue;

                int slot;
                if (!int.TryParse(key.Substring(1), out slot))
                    continue;

                string[] parts = val.Split(',');
                if (parts.Length != 3)
                    continue;

                int pan, tilt, zoom;
                if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out pan) &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out tilt) &&
                    int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out zoom))
                {
                    result[slot] = new Preset { Pan = pan, Tilt = tilt, Zoom = zoom };
                }
            }
            return result;
        }

        public static void Save(Dictionary<int, Preset> presets)
        {
            string path = FilePath();
            var lines = new List<string> { "# PTZControl presets - auto-generated, F<slot>=pan,tilt,zoom" };
            foreach (var kv in presets.OrderBy(k => k.Key))
                lines.Add(string.Format(CultureInfo.InvariantCulture, "F{0}={1},{2},{3}", kv.Key, kv.Value.Pan, kv.Value.Tilt, kv.Value.Zoom));
            File.WriteAllLines(path, lines);
        }
    }

    #endregion

    internal struct HotkeyConfig
    {
        public int DeviceIndex, PanStep, TiltStep, ZoomStep;
    }

    #region Hotkey config persistence (device index + pan/tilt/zoom step sizes)

    internal static class ConfigStore
    {
        static string FilePath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PTZControl");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "config.ini");
        }

        public static HotkeyConfig LoadOrDefault()
        {
            var cfg = new HotkeyConfig { DeviceIndex = 0, PanStep = 1, TiltStep = 1, ZoomStep = 10 };
            string path = FilePath();
            if (!File.Exists(path))
                return cfg;

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;

                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                int parsed;
                if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    continue;

                if (key.Equals("DeviceIndex", StringComparison.OrdinalIgnoreCase)) cfg.DeviceIndex = parsed;
                else if (key.Equals("PanStep", StringComparison.OrdinalIgnoreCase)) cfg.PanStep = parsed;
                else if (key.Equals("TiltStep", StringComparison.OrdinalIgnoreCase)) cfg.TiltStep = parsed;
                else if (key.Equals("ZoomStep", StringComparison.OrdinalIgnoreCase)) cfg.ZoomStep = parsed;
            }
            return cfg;
        }

        public static void Save(HotkeyConfig cfg)
        {
            string path = FilePath();
            var lines = new List<string>
            {
                "# PTZControl hotkey config - auto-generated",
                "DeviceIndex=" + cfg.DeviceIndex.ToString(CultureInfo.InvariantCulture),
                "PanStep=" + cfg.PanStep.ToString(CultureInfo.InvariantCulture),
                "TiltStep=" + cfg.TiltStep.ToString(CultureInfo.InvariantCulture),
                "ZoomStep=" + cfg.ZoomStep.ToString(CultureInfo.InvariantCulture)
            };
            File.WriteAllLines(path, lines);
        }
    }

    #endregion

    #region Core camera logic (shared by CLI mode and hotkey mode)

    internal static class Camera
    {
        static readonly Guid CLSID_VideoInputDeviceCategory = new Guid("860BB310-5D01-11d0-BD3B-00A0C911CE86");

        public static DeviceInfo[] EnumerateDevices()
        {
            var devEnum = (ICreateDevEnum)new SystemDeviceEnum();
            IEnumMoniker enumMoniker = null;
            try
            {
                Guid category = CLSID_VideoInputDeviceCategory;
                int hr = devEnum.CreateClassEnumerator(ref category, out enumMoniker, 0);
                if (hr != 0 || enumMoniker == null)
                    return new DeviceInfo[0];

                var result = new List<DeviceInfo>();
                IMoniker[] monikers = new IMoniker[1];
                IntPtr fetched = IntPtr.Zero;

                while (enumMoniker.Next(1, monikers, fetched) == 0)
                {
                    result.Add(new DeviceInfo { Moniker = monikers[0], Name = GetFriendlyName(monikers[0]) });
                }

                return result.ToArray();
            }
            finally
            {
                if (enumMoniker != null && Marshal.IsComObject(enumMoniker))
                {
                    try { Marshal.ReleaseComObject(enumMoniker); } catch { }
                }
                if (Marshal.IsComObject(devEnum))
                {
                    try { Marshal.ReleaseComObject(devEnum); } catch { }
                }
            }
        }

        // Reads the device's FriendlyName from its property bag, releasing the
        // bag explicitly so it doesn't fall to the GC finalizer (unsafe with
        // some camera drivers - see CloseCameraControl below).
        static string GetFriendlyName(IMoniker moniker)
        {
            object bagObj = null;
            try
            {
                Guid propertyBagGuid = typeof(IPropertyBag).GUID;
                try
                {
                    moniker.BindToStorage(null, null, ref propertyBagGuid, out bagObj);
                }
                catch (COMException)
                {
                    return "(unknown)";
                }

                var bag = bagObj as IPropertyBag;
                if (bag == null)
                    return "(unknown)";

                object val = null;
                int readHr = bag.Read("FriendlyName", ref val, IntPtr.Zero);
                if (readHr != 0 || val == null)
                    return "(unknown)";

                return val.ToString();
            }
            finally
            {
                if (bagObj != null && Marshal.IsComObject(bagObj))
                {
                    try { Marshal.ReleaseComObject(bagObj); } catch { }
                }
            }
        }

        public static IAMCameraControl OpenCameraControl(int deviceIndex, out string deviceName)
        {
            DeviceInfo[] devices = EnumerateDevices();
            try
            {
                if (deviceIndex < 0 || deviceIndex >= devices.Length)
                    throw new Exception(string.Format("Device index {0} out of range (found {1} device(s); run 'list').",
                        deviceIndex, devices.Length));

                DeviceInfo dev = devices[deviceIndex];
                deviceName = dev.Name;

                Guid iid = typeof(IBaseFilterMarker).GUID;
                object filterObj;
                dev.Moniker.BindToObject(null, null, ref iid, out filterObj);

                var camControl = filterObj as IAMCameraControl;

                // BindToObject and the QI above each AddRef the filter. Release
                // the IBaseFilter RCW so only the IAMCameraControl RCW (returned
                // here, released by CloseCameraControl) keeps it alive.
                if (filterObj != null && Marshal.IsComObject(filterObj))
                    Marshal.ReleaseComObject(filterObj);

                if (camControl == null)
                    throw new Exception(string.Format("Device '{0}' does not support IAMCameraControl (no PTZ controls).", dev.Name));

                return camControl;
            }
            finally
            {
                // Monikers have served their purpose (binding the selected
                // device); release them all instead of leaving them to the GC.
                foreach (DeviceInfo d in devices)
                {
                    if (d.Moniker != null && Marshal.IsComObject(d.Moniker))
                    {
                        try { Marshal.ReleaseComObject(d.Moniker); } catch { }
                    }
                }
            }
        }

        // Releases a COM camera control connection obtained from OpenCameraControl.
        // Always call this explicitly rather than relying on the GC/finalizer to
        // release DirectShow COM objects - finalizing them from a background
        // finalizer thread instead of the thread that created them is unsafe with
        // some camera drivers and can corrupt the process heap.
        public static void CloseCameraControl(IAMCameraControl cc)
        {
            if (cc != null && Marshal.IsComObject(cc))
            {
                try { Marshal.ReleaseComObject(cc); } catch { }
            }
        }

        public static string GetStatusText(int deviceIndex)
        {
            string name;
            IAMCameraControl cc = OpenCameraControl(deviceIndex, out name);
            try
            {
                return GetStatusTextCore(cc, deviceIndex, name);
            }
            finally
            {
                CloseCameraControl(cc);
            }
        }

        internal static string GetStatusTextCore(IAMCameraControl cc, int deviceIndex, string deviceName)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Device " + deviceIndex + ": " + deviceName);

            foreach (CameraControlProperty prop in Enum.GetValues(typeof(CameraControlProperty)))
            {
                int min, max, step, def; CameraControlFlags caps;
                int rangeHr = cc.GetRange(prop, out min, out max, out step, out def, out caps);
                if (rangeHr != 0)
                {
                    sb.AppendLine(string.Format("  {0,-9} not supported", prop));
                    continue;
                }
                int value; CameraControlFlags flags;
                int getHr = cc.Get(prop, out value, out flags);
                if (getHr != 0)
                {
                    sb.AppendLine(string.Format("  {0,-9} read failed (hr=0x{1:X8})", prop, getHr));
                    continue;
                }
                sb.AppendLine(string.Format("  {0,-9} value={1,-6} range=[{2},{3}] step={4} default={5}",
                    prop, value, min, max, step, def));
            }
            return sb.ToString();
        }

        public static void SetAbsolute(int deviceIndex, CameraControlProperty prop, int value, out string message)
        {
            string unusedName;
            IAMCameraControl cc = OpenCameraControl(deviceIndex, out unusedName);
            try
            {
                SetAbsoluteCore(cc, prop, value, out message);
            }
            finally
            {
                CloseCameraControl(cc);
            }
        }

        internal static void SetAbsoluteCore(IAMCameraControl cc, CameraControlProperty prop, int value, out string message)
        {
            int min, max, step, def; CameraControlFlags caps;
            int rangeHr = cc.GetRange(prop, out min, out max, out step, out def, out caps);
            if (rangeHr != 0)
                throw new Exception(prop + " is not supported on this device.");

            if (min == max)
            {
                message = string.Format("{0} has no range to move in right now (at min/max zoom crop) - doing nothing.", prop);
                return;
            }

            int snapped = ClampAndSnap(value, min, max, step);
            int hr = cc.Set(prop, snapped, CameraControlFlags.Manual);
            if (hr != 0)
                throw new Exception(string.Format("Set failed (hr=0x{0:X8}).", hr));

            message = string.Format("{0} = {1} (requested {2}, range [{3},{4}])", prop, snapped, value, min, max);
        }

        public static void MoveRelative(int deviceIndex, CameraControlProperty prop, int delta, out string message)
        {
            string unusedName;
            IAMCameraControl cc = OpenCameraControl(deviceIndex, out unusedName);
            try
            {
                MoveRelativeCore(cc, prop, delta, out message);
            }
            finally
            {
                CloseCameraControl(cc);
            }
        }

        internal static void MoveRelativeCore(IAMCameraControl cc, CameraControlProperty prop, int delta, out string message)
        {
            int min, max, step, def; CameraControlFlags caps;
            int rangeHr = cc.GetRange(prop, out min, out max, out step, out def, out caps);
            if (rangeHr != 0)
                throw new Exception(prop + " is not supported on this device.");

            if (min == max)
            {
                message = string.Format("{0} has no range to move in right now (at min/max zoom crop) - doing nothing.", prop);
                return;
            }

            int current = ReadValue(cc, prop);

            // Compute in long to avoid unchecked overflow on current + delta,
            // clamp to the property's range, then snap to the driver's step grid.
            long targetLong = (long)current + delta;
            int target = ClampAndSnap(targetLong < min ? min : targetLong > max ? max : (int)targetLong, min, max, step);

            int hr = cc.Set(prop, target, CameraControlFlags.Manual);
            if (hr != 0)
                throw new Exception(string.Format("Set failed (hr=0x{0:X8}).", hr));

            message = string.Format("{0}: {1} -> {2} (range [{3},{4}])", prop, current, target, min, max);
        }

        public static void ResetProperty(int deviceIndex, CameraControlProperty prop, out string message)
        {
            string unusedName;
            IAMCameraControl cc = OpenCameraControl(deviceIndex, out unusedName);
            try
            {
                ResetPropertyCore(cc, prop, out message);
            }
            finally
            {
                CloseCameraControl(cc);
            }
        }

        internal static void ResetPropertyCore(IAMCameraControl cc, CameraControlProperty prop, out string message)
        {
            int min, max, step, def; CameraControlFlags caps;
            int rangeHr = cc.GetRange(prop, out min, out max, out step, out def, out caps);
            if (rangeHr != 0)
                throw new Exception(prop + " is not supported on this device.");

            int hr = cc.Set(prop, def, CameraControlFlags.Manual);
            if (hr != 0)
                throw new Exception(string.Format("Set failed (hr=0x{0:X8}).", hr));

            message = string.Format("{0} reset to default ({1})", prop, def);
        }

        public static void SavePreset(int deviceIndex, int slot, out string message)
        {
            string unusedName;
            IAMCameraControl cc = OpenCameraControl(deviceIndex, out unusedName);
            try
            {
                SavePresetCore(cc, slot, out message);
            }
            finally
            {
                CloseCameraControl(cc);
            }
        }

        internal static void SavePresetCore(IAMCameraControl cc, int slot, out string message)
        {
            int pan = ReadValue(cc, CameraControlProperty.Pan);
            int tilt = ReadValue(cc, CameraControlProperty.Tilt);
            int zoom = ReadValue(cc, CameraControlProperty.Zoom);

            var presets = PresetStore.Load();
            presets[slot] = new Preset { Pan = pan, Tilt = tilt, Zoom = zoom };
            PresetStore.Save(presets);

            message = string.Format("Saved preset F{0}: pan={1}, tilt={2}, zoom={3}", slot, pan, tilt, zoom);
        }

        // Reads a property value, throwing on failure instead of returning a
        // garbage 0 that would silently corrupt a saved preset.
        static int ReadValue(IAMCameraControl cc, CameraControlProperty prop)
        {
            int value; CameraControlFlags flags;
            int hr = cc.Get(prop, out value, out flags);
            if (hr != 0)
                throw new Exception(string.Format("Get {0} failed (hr=0x{1:X8}).", prop, hr));
            return value;
        }

        public static void RecallPreset(int deviceIndex, int slot, out string message)
        {
            var presets = PresetStore.Load();
            Preset p;
            if (!presets.TryGetValue(slot, out p))
            {
                message = string.Format("No preset saved on F{0}.", slot);
                return;
            }

            string unusedName;
            IAMCameraControl cc = OpenCameraControl(deviceIndex, out unusedName);
            try
            {
                RecallPresetCore(cc, slot, out message);
            }
            finally
            {
                CloseCameraControl(cc);
            }
        }

        internal static void RecallPresetCore(IAMCameraControl cc, int slot, out string message)
        {
            var presets = PresetStore.Load();
            Preset p;
            if (!presets.TryGetValue(slot, out p))
            {
                message = string.Format("No preset saved on F{0}.", slot);
                return;
            }

            // Set zoom first so pan/tilt are applied against the crop range
            // that matches the target zoom level.
            string zMsg, pMsg, tMsg;
            SetAbsoluteCore(cc, CameraControlProperty.Zoom, p.Zoom, out zMsg);
            SetAbsoluteCore(cc, CameraControlProperty.Pan, p.Pan, out pMsg);
            SetAbsoluteCore(cc, CameraControlProperty.Tilt, p.Tilt, out tMsg);

            message = string.Format("Recalled preset F{0}: pan={1}, tilt={2}, zoom={3}", slot, p.Pan, p.Tilt, p.Zoom);
        }

        public static string ListPresetsText()
        {
            var presets = PresetStore.Load();
            if (presets.Count == 0)
                return "No presets saved.";

            var sb = new System.Text.StringBuilder();
            foreach (var kv in presets.OrderBy(k => k.Key))
                sb.AppendLine(string.Format("F{0}: pan={1}, tilt={2}, zoom={3}", kv.Key, kv.Value.Pan, kv.Value.Tilt, kv.Value.Zoom));
            return sb.ToString();
        }

        public static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        // Clamps to [min,max] and snaps to the driver's step grid (steppingDelta),
        // anchored at the range minimum, so Set() doesn't fail with E_INVALIDARG
        // when the value isn't a multiple of the camera's step size.
        public static int ClampAndSnap(int v, int min, int max, int step)
        {
            int clamped = Clamp(v, min, max);
            if (step <= 1)
                return clamped;
            int snapped = clamped - ((clamped - min) % step);
            return Clamp(snapped, min, max);
        }

        public static CameraControlProperty ParseProperty(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "pan": return CameraControlProperty.Pan;
                case "tilt": return CameraControlProperty.Tilt;
                case "roll": return CameraControlProperty.Roll;
                case "zoom": return CameraControlProperty.Zoom;
                case "exposure": return CameraControlProperty.Exposure;
                case "iris": return CameraControlProperty.Iris;
                case "focus": return CameraControlProperty.Focus;
                default: throw new ArgumentException("Unknown property: " + s);
            }
        }
    }

    #endregion

    // Holds one open IAMCameraControl connection for the lifetime of the hotkey
    // daemon, instead of opening/closing a fresh COM connection on every single
    // hotkey press. Repeatedly creating and abandoning DirectShow COM objects
    // (leaving them for the GC finalizer thread to release later) is what was
    // causing STATUS_HEAP_CORRUPTION crashes under heavy hotkey use - some
    // camera drivers are not safe to release from a thread other than the one
    // that opened them. Dispose() releases the connection explicitly and
    // synchronously on the same (STA UI) thread that created it.
    internal sealed class CameraSession : IDisposable
    {
        readonly IAMCameraControl cc;
        readonly int deviceIndex;
        readonly string deviceName;
        bool disposed;

        public CameraSession(int deviceIndex)
        {
            this.deviceIndex = deviceIndex;
            string name;
            cc = Camera.OpenCameraControl(deviceIndex, out name);
            deviceName = name;
        }

        public string DeviceName { get { return deviceName; } }

        public string GetStatusText()
        {
            return Camera.GetStatusTextCore(cc, deviceIndex, deviceName);
        }

        public void SetAbsolute(CameraControlProperty prop, int value, out string message)
        {
            Camera.SetAbsoluteCore(cc, prop, value, out message);
        }

        public void MoveRelative(CameraControlProperty prop, int delta, out string message)
        {
            Camera.MoveRelativeCore(cc, prop, delta, out message);
        }

        public void ResetProperty(CameraControlProperty prop, out string message)
        {
            Camera.ResetPropertyCore(cc, prop, out message);
        }

        public void SavePreset(int slot, out string message)
        {
            Camera.SavePresetCore(cc, slot, out message);
        }

        public void RecallPreset(int slot, out string message)
        {
            Camera.RecallPresetCore(cc, slot, out message);
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            Camera.CloseCameraControl(cc);
        }
    }

    #region Hotkey daemon (tray icon + global hotkeys, no AutoHotkey needed)

    internal class HotkeyForm : Form
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        const int WM_HOTKEY = 0x0312;
        const uint MOD_ALT = 0x1;
        const uint MOD_CONTROL = 0x2;
        const uint MOD_SHIFT = 0x4;

        const uint VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
        const uint VK_PRIOR = 0x21, VK_NEXT = 0x22;   // PageUp / PageDown
        const uint VK_HOME = 0x24, VK_END = 0x23;
        const uint VK_F1 = 0x70;                      // VK_F1..VK_F8 are contiguous

        readonly int deviceIndex;
        readonly int panStep, tiltStep, zoomStep;
        readonly Dictionary<int, Action> handlers = new Dictionary<int, Action>();
        NotifyIcon trayIcon;
        Icon ownTrayIcon; // set only when we extracted (and therefore own) the icon; SystemIcons.Application is shared and must not be disposed
        CameraSession session; // one persistent COM connection, reused for every hotkey
        int nextId = 1;
        string discardMsg; // scratch var for out params we don't need the value of

        public HotkeyForm(int deviceIndex, int panStep, int tiltStep, int zoomStep)
        {
            this.deviceIndex = deviceIndex;
            this.panStep = panStep;
            this.tiltStep = tiltStep;
            this.zoomStep = zoomStep;

            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Opacity = 0;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Hide();
            SetupTrayIcon();

            try
            {
                session = new CameraSession(deviceIndex);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the camera (device index " + deviceIndex + "):\n" + ex.Message,
                    "PTZ Control", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
                return;
            }

            RegisterAll();
        }

        void SetupTrayIcon()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Status", null, (s, e) => ShowStatus());
            menu.Items.Add("List presets", null, (s, e) =>
                trayIcon.ShowBalloonTip(4000, "PTZ Presets", Camera.ListPresetsText(), ToolTipIcon.Info));
            menu.Items.Add("Exit", null, (s, e) => Application.Exit());

            // Use the icon baked into this exe (via the /win32icon compile flag)
            // for the tray icon too, so they match. Falls back to the generic
            // system icon if none was compiled in or extraction fails.
            Icon trayIconImage;
            try
            {
                trayIconImage = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (trayIconImage != null)
                    ownTrayIcon = trayIconImage; // we own this one; dispose it on close
                else
                    trayIconImage = SystemIcons.Application;
            }
            catch
            {
                trayIconImage = SystemIcons.Application;
            }

            trayIcon = new NotifyIcon
            {
                Icon = trayIconImage,
                Visible = true,
                Text = "PTZ Control (arrows=pan/tilt, PgUp/PgDn=zoom, F1-F8=presets)",
                ContextMenuStrip = menu
            };
            trayIcon.DoubleClick += (s, e) => ShowStatus();
        }

        void Add(uint modifiers, uint vk, Action action)
        {
            int id = nextId++;
            if (RegisterHotKey(this.Handle, id, modifiers, vk))
                handlers[id] = action;
            else
                if (trayIcon != null)
                    trayIcon.ShowBalloonTip(3000, "PTZ Control", "Failed to register a hotkey (already in use?)", ToolTipIcon.Warning);
        }

        void RegisterAll()
        {
            uint mods = MOD_CONTROL | MOD_ALT;

            Add(mods, VK_LEFT, () => Do(() => session.MoveRelative(CameraControlProperty.Pan, -panStep, out discardMsg)));
            Add(mods, VK_RIGHT, () => Do(() => session.MoveRelative(CameraControlProperty.Pan, panStep, out discardMsg)));
            Add(mods, VK_UP, () => Do(() => session.MoveRelative(CameraControlProperty.Tilt, tiltStep, out discardMsg)));
            Add(mods, VK_DOWN, () => Do(() => session.MoveRelative(CameraControlProperty.Tilt, -tiltStep, out discardMsg)));
            Add(mods, VK_PRIOR, () => Do(() => session.MoveRelative(CameraControlProperty.Zoom, zoomStep, out discardMsg)));
            Add(mods, VK_NEXT, () => Do(() => session.MoveRelative(CameraControlProperty.Zoom, -zoomStep, out discardMsg)));
            Add(mods, VK_HOME, () => Do(() =>
            {
                session.ResetProperty(CameraControlProperty.Zoom, out discardMsg);
                session.ResetProperty(CameraControlProperty.Pan, out discardMsg);
                session.ResetProperty(CameraControlProperty.Tilt, out discardMsg);
                trayIcon.ShowBalloonTip(1500, "PTZ Control", "Pan/Tilt/Zoom reset to defaults", ToolTipIcon.Info);
            }));
            Add(mods, VK_END, () => ShowStatus());

            // F1-F8: plain key recalls a preset, Ctrl+Alt+Shift+<key> saves one.
            for (int i = 1; i <= 8; i++)
            {
                int slot = i; // capture per-iteration value
                uint vk = VK_F1 + (uint)(slot - 1);

                Add(0, vk, () => Do(() => session.RecallPreset(slot, out discardMsg)));

                Add(MOD_CONTROL | MOD_ALT | MOD_SHIFT, vk, () => Do(() =>
                {
                    string msg;
                    session.SavePreset(slot, out msg);
                    trayIcon.ShowBalloonTip(1200, "PTZ Control", msg, ToolTipIcon.Info);
                }));
            }
        }

        void Do(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                trayIcon.ShowBalloonTip(3000, "PTZ Control error", ex.Message, ToolTipIcon.Error);
            }
        }

        void ShowStatus()
        {
            try
            {
                string status = session.GetStatusText();
                trayIcon.ShowBalloonTip(4000, "PTZ Status", status, ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                trayIcon.ShowBalloonTip(3000, "PTZ Control error", ex.Message, ToolTipIcon.Error);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                Action action;
                if (handlers.TryGetValue(id, out action))
                    action();
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            foreach (int id in handlers.Keys)
                UnregisterHotKey(this.Handle, id);
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
            if (ownTrayIcon != null)
            {
                ownTrayIcon.Dispose();
                ownTrayIcon = null;
            }
            if (session != null)
            {
                session.Dispose();
                session = null;
            }
            base.OnFormClosed(e);
        }
    }

    #endregion

    class Program
    {
        // Belt-and-suspenders console hiding for hotkey/daemon mode:
        // /target:winexe (see the compile comment above) should mean no console
        // is ever auto-created when double-clicking the exe, but if it's ever
        // rebuilt without that flag, Windows will still allocate one for a
        // console-subsystem app - so we explicitly detach from it before
        // starting the hotkey daemon, as a guaranteed fallback. FreeConsole()
        // (rather than ShowWindow/SW_HIDE) is used deliberately: on modern
        // Windows the console window is actually owned by a separate conhost.exe
        // process, so merely hiding it can leave a stray taskbar entry behind.
        // Since this console (if one exists at all) belongs to us exclusively,
        // detaching from it makes Windows close it outright.
        [DllImport("kernel32.dll")]
        static extern bool FreeConsole();

        // For the CLI commands (list/status/set/move/etc.), reattach to
        // whichever console launched us so Console.WriteLine output still
        // shows up there as expected.
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int dwProcessId);
        const int ATTACH_PARENT_PROCESS = -1;

        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0 || args[0].ToLowerInvariant() == "hotkeys")
                {
                    // Detach from our own console window, if the OS gave us one,
                    // before doing anything else. FreeConsole() only ever affects
                    // a console WE own (freshly allocated for this process) - we
                    // deliberately do NOT call AttachConsole in this branch, so
                    // we never end up detaching some other process's console.
                    FreeConsole();

                    // Start from whatever was last saved (or built-in defaults if
                    // nothing's been saved yet), then apply any values given on
                    // the command line on top - and persist those so the same
                    // values are used automatically next time, with no arguments.
                    int startIdx = (args.Length > 0 && args[0].ToLowerInvariant() == "hotkeys") ? 1 : 0;
                    HotkeyConfig cfg = ConfigStore.LoadOrDefault();
                    bool changed = false;

                    if (args.Length > startIdx) { cfg.DeviceIndex = int.Parse(args[startIdx]); changed = true; }
                    if (args.Length > startIdx + 1) { cfg.PanStep = int.Parse(args[startIdx + 1]); changed = true; }
                    if (args.Length > startIdx + 2) { cfg.TiltStep = int.Parse(args[startIdx + 2]); changed = true; }
                    if (args.Length > startIdx + 3) { cfg.ZoomStep = int.Parse(args[startIdx + 3]); changed = true; }

                    if (changed)
                        ConfigStore.Save(cfg);

                    Application.EnableVisualStyles();
                    Application.Run(new HotkeyForm(cfg.DeviceIndex, cfg.PanStep, cfg.TiltStep, cfg.ZoomStep));
                    return 0;
                }

                AttachConsole(ATTACH_PARENT_PROCESS); // reattach to the launching console for CLI output; no-op if none

                string action = args[0].ToLowerInvariant();

                if (action == "list")
                {
                    foreach (var line in ListLines()) Console.WriteLine(line);
                    return 0;
                }

                if (action == "status")
                {
                    int devIdx = args.Length > 1 ? int.Parse(args[1]) : 0;
                    Console.Write(Camera.GetStatusText(devIdx));
                    return 0;
                }

                if (action == "presets")
                {
                    Console.Write(Camera.ListPresetsText());
                    return 0;
                }

                if (action == "config")
                {
                    HotkeyConfig cfg = ConfigStore.LoadOrDefault();
                    Console.WriteLine(string.Format(
                        "deviceIndex={0}, panStep={1}, tiltStep={2}, zoomStep={3}",
                        cfg.DeviceIndex, cfg.PanStep, cfg.TiltStep, cfg.ZoomStep));
                    return 0;
                }

                if (action == "setsteps")
                {
                    if (args.Length < 5) { PrintUsage(); return 1; }
                    HotkeyConfig cfg = new HotkeyConfig
                    {
                        DeviceIndex = int.Parse(args[1]),
                        PanStep = int.Parse(args[2]),
                        TiltStep = int.Parse(args[3]),
                        ZoomStep = int.Parse(args[4])
                    };
                    ConfigStore.Save(cfg);
                    Console.WriteLine(string.Format(
                        "Saved: deviceIndex={0}, panStep={1}, tiltStep={2}, zoomStep={3}",
                        cfg.DeviceIndex, cfg.PanStep, cfg.TiltStep, cfg.ZoomStep));
                    return 0;
                }

                if (action == "presetsave" || action == "presetload")
                {
                    if (args.Length < 2) { PrintUsage(); return 1; }
                    int slot = int.Parse(args[1]);
                    int devIdx = args.Length > 2 ? int.Parse(args[2]) : 0;
                    string message;

                    if (action == "presetsave")
                        Camera.SavePreset(devIdx, slot, out message);
                    else
                        Camera.RecallPreset(devIdx, slot, out message);

                    Console.WriteLine(message);
                    return 0;
                }

                if (action == "set" || action == "move" || action == "reset")
                {
                    if (args.Length < 2) { PrintUsage(); return 1; }
                    CameraControlProperty prop = Camera.ParseProperty(args[1]);
                    string message;

                    if (action == "reset")
                    {
                        int devIdx = args.Length > 2 ? int.Parse(args[2]) : 0;
                        Camera.ResetProperty(devIdx, prop, out message);
                        Console.WriteLine(message);
                        return 0;
                    }

                    if (args.Length < 3) { PrintUsage(); return 1; }
                    int value = int.Parse(args[2]);
                    int deviceIndex = args.Length > 3 ? int.Parse(args[3]) : 0;

                    if (action == "set")
                        Camera.SetAbsolute(deviceIndex, prop, value, out message);
                    else
                        Camera.MoveRelative(deviceIndex, prop, value, out message);

                    Console.WriteLine(message);
                    return 0;
                }

                PrintUsage();
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                return 2;
            }
        }

        static IEnumerable<string> ListLines()
        {
            DeviceInfo[] devices = Camera.EnumerateDevices();
            if (devices.Length == 0)
            {
                yield return "No video capture devices found.";
                yield break;
            }
            for (int i = 0; i < devices.Length; i++)
                yield return i + ": " + devices[i].Name;
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  PTZControl.exe list");
            Console.WriteLine("  PTZControl.exe status [deviceIndex]");
            Console.WriteLine("  PTZControl.exe set   <pan|tilt|zoom|roll|exposure|iris|focus> <absoluteValue> [deviceIndex]");
            Console.WriteLine("  PTZControl.exe move  <pan|tilt|zoom|roll|exposure|iris|focus> <delta>        [deviceIndex]");
            Console.WriteLine("  PTZControl.exe reset <pan|tilt|zoom|roll|exposure|iris|focus>                [deviceIndex]");
            Console.WriteLine("  PTZControl.exe presetsave <1-8> [deviceIndex]");
            Console.WriteLine("  PTZControl.exe presetload <1-8> [deviceIndex]");
            Console.WriteLine("  PTZControl.exe presets");
            Console.WriteLine("  PTZControl.exe config");
            Console.WriteLine("  PTZControl.exe setsteps <deviceIndex> <panStep> <tiltStep> <zoomStep>");
            Console.WriteLine("  PTZControl.exe hotkeys [deviceIndex] [panStep] [tiltStep] [zoomStep]");
            Console.WriteLine("  PTZControl.exe                                    (uses last-saved config, or defaults)");
        }
    }
}
