﻿using UnityEngine;
using System.Collections;
using System;
using System.Runtime.InteropServices;

namespace WiimoteApi { 
public class WiimoteManager
{

    public const ushort vendor_id = 0x057e;
    public const ushort product_id_wiimote = 0x0306;
    public const ushort product_id_wiimoteplus = 0x0330;

    public static bool RumbleOn = false;

    private static IntPtr hidapi_wiimote = IntPtr.Zero;
    private static bool wiimoteplus;
    public static Wiimote State = new Wiimote();

    private static InputDataType last_report_type = InputDataType.REPORT_BUTTONS;
    private static bool expecting_status_report = false;

    // ------------- RAW HIDAPI INTERFACE ------------- //

    public static bool FindWiimote(bool wiimoteplus)
    {
        if (hidapi_wiimote != IntPtr.Zero)
            HIDapi.hid_close(hidapi_wiimote);

        hidapi_wiimote = HIDapi.hid_open(vendor_id, wiimoteplus ? product_id_wiimoteplus : product_id_wiimote, null);
        WiimoteManager.wiimoteplus = wiimoteplus;

        return hidapi_wiimote != IntPtr.Zero;
    }

    public static void Cleanup()
    {
        if (hidapi_wiimote != IntPtr.Zero)
            HIDapi.hid_close(hidapi_wiimote);
        hidapi_wiimote = IntPtr.Zero;
    }

    public static bool HasWiimote()
    {
        return !(hidapi_wiimote == null || hidapi_wiimote == IntPtr.Zero);
    }

    public static int SendRaw(byte[] data)
    {
        if (hidapi_wiimote == IntPtr.Zero) return -1;

        return HIDapi.hid_write(hidapi_wiimote, data, new UIntPtr(Convert.ToUInt32(data.Length)));
    }

    public static int RecieveRaw(byte[] buf)
    {
        if (hidapi_wiimote == IntPtr.Zero) return -1;

        HIDapi.hid_set_nonblocking(hidapi_wiimote, 1);
        int res = HIDapi.hid_read(hidapi_wiimote, buf, new UIntPtr(Convert.ToUInt32(buf.Length)));

        return res;
    }

    // ------------- WIIMOTE SPECIFIC UTILITIES ------------- //

    #region Setups

    public static bool SetupIRCamera(IRDataType type = IRDataType.EXTENDED)
    {
        int res;
        // 1. Enable IR Camera (Send 0x04 to Output Report 0x13)
        // 2. Enable IR Camera 2 (Send 0x04 to Output Report 0x1a)
        res = SendIRCameraEnable(true);
        if (res < 0) return false;
        // 3. Write 0x08 to register 0xb00030
        res = SendRegisterWriteRequest(RegisterType.CONTROL, 0xb00030, new byte[] { 0x08 });
        if (res < 0) return false;
        // 4. Write Sensitivity Block 1 to registers at 0xb00000
        // Wii sensitivity level 3:
        // 02 00 00 71 01 00 aa 00 64
        res = SendRegisterWriteRequest(RegisterType.CONTROL, 0xb00000, 
            new byte[] { 0x02, 0x00, 0x00, 0x71, 0x01, 0x00, 0xaa, 0x00, 0x64 });
        if (res < 0) return false;
        // 5. Write Sensitivity Block 2 to registers at 0xb0001a
        // Wii sensitivity level 3: 
        // 63 03
        res = SendRegisterWriteRequest(RegisterType.CONTROL, 0xb0001a, new byte[] { 0x63, 0x03 });
        if (res < 0) return false;
        // 6. Write Mode Number to register 0xb00033
        res = SendRegisterWriteRequest(RegisterType.CONTROL, 0xb00033, new byte[] { (byte)type });
        if (res < 0) return false;
        // 7. Write 0x08 to register 0xb00030 (again)
        res = SendRegisterWriteRequest(RegisterType.CONTROL, 0xb00030, new byte[] { 0x08 });
        if (res < 0) return false;

        switch (type)
        {
            case IRDataType.BASIC:
                res = SendDataReportMode(InputDataType.REPORT_BUTTONS_ACCEL_IR10_EXT6);
                break;
            case IRDataType.EXTENDED:
                res = SendDataReportMode(InputDataType.REPORT_BUTTONS_ACCEL_IR12);
                break;
            case IRDataType.FULL:
                Debug.LogWarning("Interleaved data reporting is currently not supported.");
                res = SendDataReportMode(InputDataType.REPORT_INTERLEAVED);
                break;
        }
        
        if (res < 0) return false;
        return true;
    }

    #endregion

    #region Write
    public static int SendWithType(OutputDataType type, byte[] data)
    {
        byte[] final = new byte[data.Length + 1];
        final[0] = (byte)type;

        for (int x = 0; x < data.Length; x++)
            final[x + 1] = data[x];

        if (RumbleOn)
            final[1] |= 0x01;

        int res = SendRaw(final);

        if (res < 0) Debug.LogError("HidAPI reports error " + res + " on write: " + Marshal.PtrToStringUni(HIDapi.hid_error(hidapi_wiimote)));
        else Debug.Log("Sent " + res + "b: [" + final[0].ToString("X").PadLeft(2, '0') + "] " + BitConverter.ToString(data));

        return res;
    }

    public static int SendPlayerLED(bool led1, bool led2, bool led3, bool led4)
    {
        byte mask = 0;
        if (led1) mask |= 0x10;
        if (led2) mask |= 0x20;
        if (led3) mask |= 0x40;
        if (led4) mask |= 0x80;

        return SendWithType(OutputDataType.LED, new byte[] { mask });
    }

    public static int SendDataReportMode(InputDataType mode)
    {
        if (mode == InputDataType.STATUS_INFO || mode == InputDataType.READ_MEMORY_REGISTERS || mode == InputDataType.ACKNOWLEDGE_OUTPUT_REPORT)
        {
            Debug.LogError("Passed " + mode.ToString() + " to SendDataReportMode!");
            return -1;
        }

        last_report_type = mode;

        return SendWithType(OutputDataType.DATA_REPORT_MODE, new byte[] { 0x00, (byte)mode });
    }

    public static int SendIRCameraEnable(bool enabled)
    {
        byte[] mask = new byte[] { (byte)(enabled ? 0x04 : 0x00) };

        int first = SendWithType(OutputDataType.IR_CAMERA_ENABLE, mask);
        if (first < 0) return first;

        int second = SendWithType(OutputDataType.IR_CAMERA_ENABLE_2, mask);
        if (second < 0) return second;

        return first + second; // success
    }

    public static int SendSpeakerEnabled(bool enabled)
    {
        byte[] mask = new byte[] { (byte)(enabled ? 0x04 : 0x00) };

        return SendWithType(OutputDataType.SPEAKER_ENABLE, mask);
    }

    public static int SendSpeakerMuted(bool muted)
    {
        byte[] mask = new byte[] { (byte)(muted ? 0x04 : 0x00) };

        return SendWithType(OutputDataType.SPEAKER_MUTE, mask);
    }

    public static int SendStatusInfoRequest()
    {
        expecting_status_report = true;
        return SendWithType(OutputDataType.STATUS_INFO_REQUEST, new byte[] { 0x00 });
    }

    public static int SendRegisterReadRequest(RegisterType type, int offset, int size)
    {
        byte address_select = (byte)type;
        byte[] offsetArr = IntToBigEndian(offset, 3);
        byte[] sizeArr = IntToBigEndian(size, 2);

        byte[] total = new byte[] { address_select, offsetArr[0], offsetArr[1], offsetArr[2], 
            sizeArr[0], sizeArr[1] };

        return SendWithType(OutputDataType.READ_MEMORY_REGISTERS, total);
    }

    public static int SendRegisterWriteRequest(RegisterType type, int offset, byte[] data)
    {
        if (data.Length > 16) return -1;
        

        byte address_select = (byte)type;
        byte[] offsetArr = IntToBigEndian(offset,3);

        byte[] total = new byte[21];
        total[0] = address_select;
        for (int x = 0; x < 3; x++) total[x + 1] = offsetArr[x];
        total[4] = (byte)data.Length;
        for (int x = 0; x < data.Length; x++) total[x + 5] = data[x];

        return SendWithType(OutputDataType.WRITE_MEMORY_REGISTERS, total);
    }
    #endregion

    #region Read
    public static int ReadWiimoteData()
    {
        byte[] buf = new byte[22];
        int status = RecieveRaw(buf);
        if (status <= 0) return status; // Either there is some sort of error or we haven't recieved anything

        int typesize = GetInputDataTypeSize((InputDataType)buf[0]);
        byte[] data = new byte[typesize];
        for (int x = 0; x < data.Length; x++)
            data[x] = buf[x + 1];

        Debug.Log("Recieved: [" + buf[0].ToString("X").PadLeft(2, '0') + "] " + BitConverter.ToString(data));

        // Variable names used throughout the switch/case block
        byte[] buttons;
        byte[] accel;
        byte[] ext;
        byte[] ir;

        switch ((InputDataType)buf[0]) // buf[0] is the output ID byte
        {
            case InputDataType.STATUS_INFO: // done.
                buttons = new byte[] { data[0], data[1] };
                byte flags = data[2];
                byte battery_level = data[5];

                InterpretButtonData(buttons);
                State.battery_level = battery_level;

                State.battery_low = (flags & 0x01) == 0x01;
                State.ext_connected = (flags & 0x02) == 0x02;
                State.speaker_enabled = (flags & 0x04) == 0x04;
                State.ir_enabled = (flags & 0x08) == 0x08;
                State.led[0] = (flags & 0x10) == 0x10;
                State.led[1] = (flags & 0x20) == 0x20;
                State.led[2] = (flags & 0x40) == 0x40;
                State.led[3] = (flags & 0x80) == 0x80;

                if (expecting_status_report)
                    expecting_status_report = false;
                else                                        // We haven't requested any data report type, meaning a controller has connected.
                    SendDataReportMode(last_report_type);   // If we don't update the data report mode, no updates will be sent
                break;
            case InputDataType.READ_MEMORY_REGISTERS:
                // TODO
                break;
            case InputDataType.ACKNOWLEDGE_OUTPUT_REPORT:
                buttons = new byte[] { data[0], data[1] };
                InterpretButtonData(buttons);
                // TODO: doesn't do any actual error handling, or do any special code about acknowledging the output report.
                break;
            case InputDataType.REPORT_BUTTONS: // done.
                buttons = new byte[] { data[0], data[1] };
                InterpretButtonData(buttons);
                break;
            case InputDataType.REPORT_BUTTONS_ACCEL: // done.
                buttons = new byte[] { data[0], data[1] };
                InterpretButtonData(buttons);

                accel = new byte[] { data[2], data[3], data[4] };
                InterpretAccelData(buttons, accel);
                break;
            case InputDataType.REPORT_BUTTONS_EXT8: // done.
                buttons = new byte[] { data[0], data[1] };
                InterpretButtonData(buttons);

                ext = new byte[8];
                for (int x = 0; x < ext.Length; x++)
                    ext[x] = data[x + 2];

                State.extension = ext;
                break;
            case InputDataType.REPORT_BUTTONS_ACCEL_IR12: // done.
                buttons = new byte[] { data[0], data[1] };
                InterpretButtonData(buttons);

                accel = new byte[] { data[2], data[3], data[4] };
                InterpretAccelData(buttons, accel);

                ir = new byte[12];
                for (int x = 0; x < 12; x++)
                    ir[x] = data[x + 5];
                InterpretIRData12(ir);
                break;
            case InputDataType.REPORT_BUTTONS_EXT19: // done.
                buttons = new byte[] { data[0], data[1] };
                InterpretButtonData(buttons);

                ext = new byte[19];
                for (int x = 0; x < ext.Length; x++)
                    ext[x] = data[x + 2];
                State.extension = ext;
                break;
            case InputDataType.REPORT_BUTTONS_ACCEL_EXT16: // done.
                buttons = new byte[] { data[0], data[1] };
                InterpretButtonData(buttons);

                accel = new byte[] { data[2], data[3], data[4] };
                InterpretAccelData(buttons, accel);

                ext = new byte[16];
                for (int x = 0; x < ext.Length; x++)
                    ext[x] = data[x + 5];
                State.extension = ext;
                break;
            case InputDataType.REPORT_BUTTONS_IR10_EXT9: // done.
                buttons = new byte[] { data[0], data[1] };
                InterpretButtonData(buttons);

                ir = new byte[10];
                for (int x = 0; x < 10; x++)
                    ir[x] = data[x + 2];
                InterpretIRData10(ir);

                ext = new byte[9];
                for (int x = 0; x < 9; x++)
                    ext[x] = data[x + 12];
                State.extension = ext;
                break;
            case InputDataType.REPORT_BUTTONS_ACCEL_IR10_EXT6: // done.
                buttons = new byte[] { data[0], data[1] };
                InterpretButtonData(buttons);

                accel = new byte[] { data[2], data[3], data[4] };
                InterpretAccelData(buttons, accel);

                ir = new byte[10];
                for (int x = 0; x < 10; x++)
                    ir[x] = data[x + 5];
                InterpretIRData10(ir);

                ext = new byte[6];
                for (int x = 0; x < 6; x++)
                    ext[x] = data[x + 15];
                State.extension = ext;
                break;
            case InputDataType.REPORT_EXT21: // done.
                ext = new byte[21];
                for (int x = 0; x < ext.Length; x++)
                    ext[x] = data[x];
                State.extension = ext;
                break;
            case InputDataType.REPORT_INTERLEAVED:
                // TODO
                break;
            case InputDataType.REPORT_INTERLEAVED_ALT:
                // TODO
                break;
        }
        return status;
    }

    public static int GetInputDataTypeSize(InputDataType type)
    {
        switch (type)
        {
            case InputDataType.STATUS_INFO:
                return 6;
            case InputDataType.READ_MEMORY_REGISTERS:
                return 21;
            case InputDataType.ACKNOWLEDGE_OUTPUT_REPORT:
                return 4;
            case InputDataType.REPORT_BUTTONS:
                return 2;
            case InputDataType.REPORT_BUTTONS_ACCEL:
                return 5;
            case InputDataType.REPORT_BUTTONS_EXT8:
                return 10;
            case InputDataType.REPORT_BUTTONS_ACCEL_IR12:
                return 17;
            case InputDataType.REPORT_BUTTONS_EXT19:
                return 21;
            case InputDataType.REPORT_BUTTONS_ACCEL_EXT16:
                return 21;
            case InputDataType.REPORT_BUTTONS_IR10_EXT9:
                return 21;
            case InputDataType.REPORT_BUTTONS_ACCEL_IR10_EXT6:
                return 21;
            case InputDataType.REPORT_EXT21:
                return 21;
            case InputDataType.REPORT_INTERLEAVED:
                return 21;
            case InputDataType.REPORT_INTERLEAVED_ALT:
                return 21;
        }
        return 0;
    }

    public static void InterpretButtonData(byte[] data)
    {
        if (data == null || data.Length != 2) return;

        State.d_left = (data[0] & 0x01) == 0x01;
        State.d_right = (data[0] & 0x02) == 0x02;
        State.d_down = (data[0] & 0x04) == 0x04;
        State.d_up = (data[0] & 0x08) == 0x08;
        State.plus = (data[0] & 0x10) == 0x10;

        State.two = (data[1] & 0x01) == 0x01;
        State.one = (data[1] & 0x02) == 0x02;
        State.b = (data[1] & 0x04) == 0x04;
        State.a = (data[1] & 0x08) == 0x08;
        State.minus = (data[1] & 0x10) == 0x10;

        State.home = (data[1] & 0x80) == 0x80;
    }

    public static void InterpretAccelData(byte[] buttons, byte[] accel)
    {
        if (buttons == null || accel == null || buttons.Length != 2 || accel.Length != 3) return;

        State.accel[0] = ((int)accel[0] << 2) | ((buttons[0] >> 5) & 0xff);
        State.accel[1] = ((int)accel[1] << 2) | ((buttons[1] >> 4) & 0xf0);
        State.accel[2] = ((int)accel[2] << 2) | ((buttons[1] >> 5) & 0xf0);

        for (int x = 0; x < 3; x++) State.accel[x] -= 0x200; // center around zero.
    }

    public static void InterpretIRData10(byte[] data)
    {
        if (data.Length != 10) return;

        byte[] half = new byte[5];
        for (int x = 0; x < 5; x++) half[x] = data[x];
        int[,] subset = InterperetIRData10_Subset(half);
        for (int x = 0; x < 2; x++)
            for (int y = 0; y < 3; y++)
                State.ir[x, y] = subset[x, y];

        for (int x = 0; x < 5; x++) half[x] = data[x + 5];
        subset = InterperetIRData10_Subset(half);
        for (int x = 0; x < 2; x++)
            for (int y = 0; y < 3; y++)
                State.ir[x+2, y] = subset[x, y];
    }

    private static int[,] InterperetIRData10_Subset(byte[] data)
    {
        if (data.Length != 5) return new int[,] { { -1, -1, -1 }, { -1, -1, -1 } };

        int x1 = data[0];
        x1 |= ((int)(data[2] & 0x30)) << 4;
        int y1 = data[1];
        y1 |= ((int)(data[2] & 0xc0)) << 2;

        if (data[0] == 0xff && data[1] == 0xff && (data[2] & 0xf0) == 0xf0)
        {
            x1 = -1;
            y1 = -1;
        }

        int x2 = data[3];
        x2 |= ((int)(data[2] & 0x03)) << 8;
        int y2 = data[4];
        y2 |= ((int)(data[2] & 0x0c)) << 6;

        if (data[3] == 0xff && data[4] == 0xff && (data[2] & 0x0f) == 0x0f)
        {
            x1 = -1;
            y1 = -1;
        }

        return new int[,] { { x1, y1, -1 }, { x2, y2, -1 } };
    }

    public static void InterpretIRData12(byte[] data)
    {
        if (data.Length != 12) return;
        for (int x = 0; x < 4; x++)
        {
            int i = x * 3; // starting index of data
            byte[] subset = new byte[] { data[i], data[i + 1], data[i + 2] };
            int[] calc = InterpretIRData12_Subset(subset);

            State.ir[x, 0] = calc[0];
            State.ir[x, 1] = calc[1];
            State.ir[x, 2] = calc[2];
        }
    }

    private static int[] InterpretIRData12_Subset(byte[] data)
    {
        if (data.Length != 3) return new int[] { -1, -1, -1 };
        if (data[0] == 0xff && data[1] == 0xff && data[2] == 0xff) return new int[] { -1, -1, -1 };

        int x = data[0];
        x |= ((int)(data[2] & 0x30)) << 4;
        int y = data[1];
        y |= ((int)(data[2] & 0xc0)) << 2;
        int size = data[2] & 0x0f;

        return new int[] { x, y, size };
    }
    #endregion

    // ------------- UTILITY ------------- //
    public static byte[] IntToBigEndian(int input, int len)
    {
        byte[] intBytes = BitConverter.GetBytes(input);
        Array.Resize(ref intBytes, len);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(intBytes);
        
        return intBytes;
    }

    public enum RegisterType
    {
        EEPROM = 0x00, CONTROL = 0x04
    }

    /// <summary>
    /// A so-called output data type represents all data that can be sent from the host to the wiimote.
    /// This information is used by the remote to change its internal read/write state.
    /// </summary>
    public enum OutputDataType
    {
        LED = 0x11,
        DATA_REPORT_MODE = 0x12,
        IR_CAMERA_ENABLE = 0x13,
        SPEAKER_ENABLE = 0x14,
        STATUS_INFO_REQUEST = 0x15,
        WRITE_MEMORY_REGISTERS = 0x16,
        READ_MEMORY_REGISTERS = 0x17,
        SPEAKER_DATA = 0x18,
        SPEAKER_MUTE = 0x19,
        IR_CAMERA_ENABLE_2 = 0x1A
    }

    /// <summary>
    /// A so-called input data type represents all data that can be sent from the wiimote to the host.
    /// This information is used by the host as basic controller data from the wiimote.
    /// 
    /// Note that all REPORT_ types represent the actual data types that can be sent from the contoller.
    /// </summary>
    public enum InputDataType
    {
        STATUS_INFO = 0x20,
        READ_MEMORY_REGISTERS = 0x21,
        ACKNOWLEDGE_OUTPUT_REPORT = 0x22,
        REPORT_BUTTONS = 0x30,
        REPORT_BUTTONS_ACCEL = 0x31,
        REPORT_BUTTONS_EXT8 = 0x32,
        REPORT_BUTTONS_ACCEL_IR12 = 0x33,
        REPORT_BUTTONS_EXT19 = 0x34,
        REPORT_BUTTONS_ACCEL_EXT16 = 0x35,
        REPORT_BUTTONS_IR10_EXT9 = 0x36,
        REPORT_BUTTONS_ACCEL_IR10_EXT6 = 0x37,
        REPORT_EXT21 = 0x3d,
        REPORT_INTERLEAVED = 0x3e,
        REPORT_INTERLEAVED_ALT = 0x3f
    }

    /// <summary>
    /// These are the 3 types of IR data accepted by the Wiimote.  They offer more
    /// or less IR data in exchange for space for other data (such as extension
    /// controllers or accelerometer data).  The 3 types are:
    /// 
    /// BASIC:      10 bytes of data.  Contains position data for each dot only.
    ///             Works with reports 0x36 and 0x37.
    /// EXTENDED:   12 bytes of data.  Contains position and size data for each dot.
    ///             Works with report 0x33 only.
    /// FULL:       36 bytes of data.  Contains position, size, bounding box, and
    ///             intensity data for each dot.  Works with interleaved report 
    ///             0x3e/0x3f only.
    /// </summary>
    public enum IRDataType
    {
        BASIC = 1,
        EXTENDED = 3,
        FULL = 5
    }

}

[System.Serializable]
public class Wiimote
{

    public Wiimote()
    {
        led = new bool[4];
        accel = new int[3];
        ir = new int[4,3];
    }

    public bool[] led;
    // Current wiimote-space accelration, in wiimote coordinate system.
    // These are RAW values, so they may be off.  See CalibrateAccel().
    // This is only updated if the Wiimote has a report mode with Accel
    // Range:            -128 to 128
    // Up/Down:          +Z/-Z
    // Left/Right:       +X/-Z
    // Forward/Backward: -Y/+Y
    public int[] accel;
    
    // Current wiimote RAW IR data.  Size = [4,3].  Wiimote IR data can
    // detect up to four IR dots.  Data = -1 if it is inapplicable (for
    // example, if there are less than four dots, or if size data is
    // unavailable).
    // This is only updated if the Wiimote has a report mode with IR
    //
    //        | Position X | Position Y |  Size  |
    // Range: |  0 - 1023  |  0 - 767   | 0 - 15 |
    // Index: |     0      |      1     |   2    |
    //
    // int[dot index, x (0) / y (1) / size (2)]
    public int[,] ir;

    // Raw data for any extension controllers connected to the wiimote
    // This is only updated if the Wiimote has a report mode with Extensions.
    public byte[] extension;

    // True if the Wiimote's batteries are low, as reported by the Wiimote.
    // This is only updated when the Wiimote sends status reports.
    // See also: battery_level
    public bool battery_low;
    // True if an extension controller is connected, as reported by the Wiimote.
    // This is only updated when the Wiimote sends status reports.
    public bool ext_connected;
    // True if the speaker is currently enabled, as reported by the Wiimote.
    // This is only updated when the Wiimote sends status reports.
    public bool speaker_enabled;
    // True if IR is currently enabled, as reported by the Wiimote.
    // This is only updated when the Wiimote sends status reports.
    public bool ir_enabled;

    // The current battery level, as reported by the Wiimote.
    // This is only updated when the Wiimote sends status reports.
    // See also: battery_low
    public byte battery_level;

    // Button: D-Pad Left
    public bool d_left;
    // Button: D-Pad Right
    public bool d_right;
    // Button: D-Pad Up
    public bool d_up;
    // Button: D-Pad Down
    public bool d_down;
    // Button: A
    public bool a;
    // Button: B
    public bool b;
    // Button: 1 (one)
    public bool one;
    // Button: 2 (two)
    public bool two;
    // Button: + (plus)
    public bool plus;
    // Button: - (minus)
    public bool minus;
    // Button: Home
    public bool home;

    // Calibration data for the accelerometer.  This is not reported
    // by the wiimote directly - it is instead collected from normal
    // Wiimote accelerometer data.  Here are the 3 calibration steps:
    //
    // 1. Horizontal with the A button facing up
    // 2. IR sensor down on the table so the expansion port is facing up
    // 3. Laying on its side, so the left side is facing up
    // 
    // By default it is set to experimental calibration data.
    // 
    // int[calibration step,calibration data] (size 3x3)
    public int[,] accel_calib = {
                                    { -20, -16,  83 },
                                    { -20,  80, -20 },
                                    {  84, -20, -12 }
                                };

    public void CalibrateAccel(AccelCalibrationStep step)
    {
        for (int x = 0; x < 3; x++)
            accel_calib[(int)step, x] = accel[x];
    }

    public float[] GetAccelZeroPoints()
    {
        float[] ret = new float[3];
        ret[0] = ((float)accel_calib[0, 0] / (float)accel_calib[1, 0]) / 2f;
        ret[1] = ((float)accel_calib[0, 1] / (float)accel_calib[2, 1]) / 2f;
        ret[2] = ((float)accel_calib[1, 2] / (float)accel_calib[2, 2]) / 2f;
        return ret;
    }

    // Calibrated Accelerometer Data using experimental calibration points.
    // These values are in Wiimote coordinates (in the direction of gravity)
    // Range: -1 to 1
    // Up/Down:          +Z/-Z
    // Left/Right:       +X/-Z
    // Forward/Backward: -Y/+Y
    // See Also: CalibrateAccel(), GetAccelZeroPoints(), accel[]
    public float[] GetCalibratedAccelData()
    {
        float[] o = GetAccelZeroPoints();

        float x_raw = accel[0];
        float y_raw = accel[1];
        float z_raw = accel[2];

        float[] ret = new float[3];
        ret[0] = (x_raw - o[0]) / (accel_calib[2, 0] - o[0]);
        ret[1] = (y_raw - o[1]) / (accel_calib[1, 1] - o[1]);
        ret[2] = (z_raw - o[2]) / (accel_calib[0, 2] - o[2]);
        return ret;
    }
}

public enum AccelCalibrationStep {
    A_BUTTON_UP = 0,
    EXPANSION_UP = 1,
    LEFT_SIDE_UP = 2
}
}