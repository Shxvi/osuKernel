#pragma once

#ifdef _KERNEL_MODE
#include <ntddk.h>
#else
#include <windows.h>
#include <winioctl.h>
#endif

//
// Device Interface GUID for osuKernel Driver
// {B2A7E114-1188-4F7D-B274-13B4E38DFB71}
//
#define OSUKERNEL_DEVINTERFACE_GUID_STR "{B2A7E114-1188-4F7D-B274-13B4E38DFB71}"

#ifdef INITGUID
#include <initguid.h>
DEFINE_GUID(GUID_DEVINTERFACE_OSUKERNEL,
    0xb2a7e114, 0x1188, 0x4f7d, 0xb2, 0x74, 0x13, 0xb4, 0xe3, 0x8d, 0xfb, 0x71);
#else
DEFINE_GUID(GUID_DEVINTERFACE_OSUKERNEL,
    0xb2a7e114, 0x1188, 0x4f7d, 0xb2, 0x74, 0x13, 0xb4, 0xe3, 0x8d, 0xfb, 0x71);
#endif

#define GUID_DEVINTERFACE_CTL472 GUID_DEVINTERFACE_OSUKERNEL

#define OSUKERNEL_DEVICE_NAME_W      L"\\Device\\osuKernelDevice"
#define OSUKERNEL_DOS_DEVICE_NAME_W  L"\\DosDevices\\osuKernelDevice"
#define OSUKERNEL_USER_DEVICE_NAME_W L"\\\\.\\osuKernelDevice"

#define CTL472_DEVICE_NAME_W         OSUKERNEL_DEVICE_NAME_W
#define CTL472_DOS_DEVICE_NAME_W     OSUKERNEL_DOS_DEVICE_NAME_W
#define CTL472_USER_DEVICE_NAME_W    OSUKERNEL_USER_DEVICE_NAME_W

#define FILE_DEVICE_OSUKERNEL 0x8056
#define FILE_DEVICE_CTL472    FILE_DEVICE_OSUKERNEL

//
// IOCTL definitions
//
#define IOCTL_OSUKERNEL_SET_AREA \
    CTL_CODE(FILE_DEVICE_OSUKERNEL, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS)

#define IOCTL_OSUKERNEL_GET_AREA \
    CTL_CODE(FILE_DEVICE_OSUKERNEL, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS)

#define IOCTL_OSUKERNEL_SET_CONFIG \
    CTL_CODE(FILE_DEVICE_OSUKERNEL, 0x802, METHOD_BUFFERED, FILE_ANY_ACCESS)

#define IOCTL_OSUKERNEL_GET_CONFIG \
    CTL_CODE(FILE_DEVICE_OSUKERNEL, 0x803, METHOD_BUFFERED, FILE_ANY_ACCESS)

#define IOCTL_OSUKERNEL_GET_STATS \
    CTL_CODE(FILE_DEVICE_OSUKERNEL, 0x804, METHOD_BUFFERED, FILE_ANY_ACCESS)

#define IOCTL_OSUKERNEL_RESET_STATS \
    CTL_CODE(FILE_DEVICE_OSUKERNEL, 0x805, METHOD_BUFFERED, FILE_ANY_ACCESS)

#define IOCTL_CTL472_SET_AREA    IOCTL_OSUKERNEL_SET_AREA
#define IOCTL_CTL472_GET_AREA    IOCTL_OSUKERNEL_GET_AREA
#define IOCTL_CTL472_SET_CONFIG  IOCTL_OSUKERNEL_SET_CONFIG
#define IOCTL_CTL472_GET_CONFIG  IOCTL_OSUKERNEL_GET_CONFIG
#define IOCTL_CTL472_GET_STATS   IOCTL_OSUKERNEL_GET_STATS
#define IOCTL_CTL472_RESET_STATS IOCTL_OSUKERNEL_RESET_STATS

#pragma pack(push, 1)

//
// Tablet Active Area and Screen Output Mapping Configuration
//
typedef struct _OSUKERNEL_AREA_CONFIG {
    //
    // Input Tablet Area (in raw counts: signed for negative offsets and unconstrained Dev Mode)
    //
    LONG  TabletInputX;          // X offset from top-left (counts, signed)
    LONG  TabletInputY;          // Y offset from top-left (counts, signed)
    ULONG TabletInputWidth;      // Active width (counts)
    ULONG TabletInputHeight;     // Active height (counts)

    //
    // Screen Output Mapping (in pixels or virtual desktop coords)
    //
    LONG  ScreenOutputX;         // Screen X offset (pixels)
    LONG  ScreenOutputY;         // Screen Y offset (pixels)
    ULONG ScreenOutputWidth;     // Target screen width (pixels)
    ULONG ScreenOutputHeight;    // Target screen height (pixels)

    //
    // Flags & Modes
    //
    ULONG AreaClipping;          // 1 = Clip cursor to target bounds; 0 = pass through outside
    ULONG AreaLimiting;          // 1 = Ignore strokes outside active area completely
    ULONG RotationAngle;         // 0, 90, 180, 270 degrees
    ULONG Reserved[4];
} OSUKERNEL_AREA_CONFIG, *POSUKERNEL_AREA_CONFIG;

typedef OSUKERNEL_AREA_CONFIG CTL472_AREA_CONFIG;
typedef POSUKERNEL_AREA_CONFIG PCTL472_AREA_CONFIG;

//
// Driver Performance & Response Configuration
//
typedef struct _OSUKERNEL_SETTINGS {
    ULONG LowLatencyMode;        // 1 = Direct kernel dispatch at DISPATCH_LEVEL; 0 = standard
    ULONG PressureCurveType;     // 0 = Linear, 1 = Soft, 2 = Hard, 3 = Custom Gamma
    ULONG PressureGammaFixed;    // Fixed-point gamma (e.g. 65536 = 1.0)
    ULONG TipClickThreshold;     // Raw pressure threshold to trigger click (0..2047)
    ULONG RawPassthrough;        // 1 = Bypass area mapping (raw coordinates)
    ULONG EnableSmoothing;       // 0 = Off, 1 = Moving Average, 2 = Adaptive 1-Euro / Antichatter
    ULONG SmoothingStrength;     // 0..100%
    ULONG AntichatterDeadzone;   // 0..30 raw counts
    ULONG InterpolationRate;     // 0 = Native, 1000 = 1kHz, 2000 = 2kHz, 4000 = 4kHz, 8000 = 8kHz
    ULONG DisableTipClick;       // 1 = Disable left click on pen tip contact
    ULONG EnablePressure;        // 1 = Active, 0 = Disabled (reports 0 pressure)
    ULONG Reserved[1];
} OSUKERNEL_SETTINGS, *POSUKERNEL_SETTINGS;

typedef OSUKERNEL_SETTINGS CTL472_SETTINGS;
typedef POSUKERNEL_SETTINGS PCTL472_SETTINGS;

//
// Real-Time Telemetry and Hardware Statistics
//
typedef struct _OSUKERNEL_DRIVER_STATS {
    ULONG64 TotalPackets;        // Total USB interrupt packets received
    ULONG   PacketsPerSecond;    // Measured packets per second (dynamic, adapts to actual firmware rate)
    
    // Last received raw packet data
    USHORT  LastRawX;            // Raw X (0..15200)
    USHORT  LastRawY;            // Raw Y (0..9500)
    USHORT  LastRawPressure;     // Raw Pressure (0..2047)
    UCHAR   LastButtons;         // Bit 0 = Tip, Bit 1 = Btn1, Bit 2 = Btn2, Bit 3 = Eraser
    UCHAR   InProximity;         // 1 = Pen in range, 0 = Out of range
    
    // Last transformed screen output
    LONG    LastScreenX;         // Computed Screen X (pixels)
    LONG    LastScreenY;         // Computed Screen Y (pixels)
    
    ULONG64 LastPacketTimestamp; // KeQueryPerformanceCounter / QueryPerformanceCounter timestamp
    ULONG   DroppedPackets;      // Packets dropped or out of bounds
} OSUKERNEL_DRIVER_STATS, *POSUKERNEL_DRIVER_STATS;

typedef OSUKERNEL_DRIVER_STATS CTL472_DRIVER_STATS;
typedef POSUKERNEL_DRIVER_STATS PCTL472_DRIVER_STATS;

#pragma pack(pop)
