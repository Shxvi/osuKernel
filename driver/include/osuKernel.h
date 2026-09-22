#pragma once

#include <ntddk.h>
#include <wdf.h>
#include <usb.h>
#include <usbdlib.h>
#include <wdfusb.h>

#include "osuKernel_ioctl.h"
#include "wacom_protocol.h"

//
// Trace / Debug helpers
//
#define OSUKERNEL_POOL_TAG 'nKso' // 'oskK'
#define CTL472_POOL_TAG    OSUKERNEL_POOL_TAG

#if DBG
#define KdPrintDbg(format, ...) DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_INFO_LEVEL, "[osuKernel] " format, __VA_ARGS__)
#else
#define KdPrintDbg(format, ...)
#endif

//
// Fast integer square root for continuous deadzone in kernel DPC
//
static __inline ULONG FastSqrt(ULONG val) {
    ULONG res = 0;
    ULONG bit = 1UL << 30;
    while (bit > val) bit >>= 2;
    while (bit != 0) {
        if (val >= res + bit) {
            val -= res + bit;
            res = (res >> 1) + bit;
        } else {
            res >>= 1;
        }
        bit >>= 2;
    }
    return res;
}

//
// Device Context Structure
//
typedef struct _DEVICE_CONTEXT {
    WDFDEVICE               Device;
    WDFUSBDEVICE            UsbDevice;
    WDFUSBINTERFACE         UsbInterface;
    WDFUSBPIPE              InterruptPipe;

    // Fast-path synchronization for area configuration
    WDFSPINLOCK             ConfigLock;
    OSUKERNEL_AREA_CONFIG   AreaConfig;
    OSUKERNEL_SETTINGS      Settings;

    // Statistics and Telemetry
    WDFSPINLOCK             StatsLock;
    OSUKERNEL_DRIVER_STATS  Stats;
    LARGE_INTEGER           PerfFreq;
    LARGE_INTEGER           LastPpsTimestamp;
    ULONG                   PpsCounter;

    // Cached pre-calculated fixed-point multipliers for zero-latency mapping
    // ScaleFixedX = (ScreenOutputWidth << 16) / TabletInputWidth;
    ULONG64                 ScaleFixedX;
    ULONG64                 ScaleFixedY;

    // 16.16 fixed-point rotation trigonometry (0 to 360 degrees)
    LONG                    CosAngleFixed;
    LONG                    SinAngleFixed;

    // Pre-calculated geometric constants for lock-free transformation in DPC
    LONG                    CenterX;
    LONG                    CenterY;
    LONG                    HalfW;
    LONG                    HalfH;
    LONGLONG                DeadzoneSq;

    // Filter state (antichatter & responsive smoothing)
    LONGLONG                FilteredRawX16;
    LONGLONG                FilteredRawY16;
    USHORT                  LastFilterRawX;
    USHORT                  LastFilterRawY;
    BOOLEAN                 FilterInitialized;

    // Trajectory state for sub-packet interpolation up to 8 kHz
    LONG                    InterpPrevScreenX;
    LONG                    InterpPrevScreenY;
    LONG                    InterpCurrScreenX;
    LONG                    InterpCurrScreenY;
    LARGE_INTEGER           InterpPrevTime;
    LARGE_INTEGER           InterpCurrTime;
    LONG                    InterpDeltaScreenX;
    LONG                    InterpDeltaScreenY;
    LONGLONG                InterpDeltaTicks;
    ULONG                   DispatchedCounter;

    // Synthetic mouse / input reporting context
    BOOLEAN                 IsPenDown;
    UCHAR                   LastButtonState;

} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, DeviceGetContext)

//
// Function prototypes
//
extern "C" {
    DRIVER_INITIALIZE DriverEntry;
    EVT_WDF_DRIVER_UNLOAD OsuKernelEvtDriverUnload;
}

EVT_WDF_DRIVER_DEVICE_ADD OsuKernelEvtDeviceAdd;
EVT_WDF_DEVICE_PREPARE_HARDWARE OsuKernelEvtDevicePrepareHardware;
EVT_WDF_DEVICE_RELEASE_HARDWARE OsuKernelEvtDeviceReleaseHardware;
EVT_WDF_DEVICE_D0_ENTRY OsuKernelEvtDeviceD0Entry;
EVT_WDF_DEVICE_D0_EXIT OsuKernelEvtDeviceD0Exit;

// USB continuous reader callbacks
EVT_WDF_USB_READER_COMPLETION_ROUTINE OsuKernelEvtUsbReadComplete;
EVT_WDF_USB_READERS_FAILED OsuKernelEvtUsbReadFailed;

// IOCTL Queue callbacks
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL OsuKernelEvtIoDeviceControl;

// Internal Helpers
NTSTATUS OsuKernelConfigureUsb(
    _In_ WDFDEVICE Device,
    _In_ PDEVICE_CONTEXT DevCtx
);

NTSTATUS OsuKernelSendFeatureReport(
    _In_ PDEVICE_CONTEXT DevCtx,
    _In_ UCHAR ReportId,
    _In_ UCHAR ReportVal
);

VOID OsuKernelRecalculateScales(
    _Inout_ PDEVICE_CONTEXT DevCtx
);

BOOLEAN OsuKernelTransformCoordinates(
    _In_ PDEVICE_CONTEXT DevCtx,
    _In_ USHORT RawX,
    _In_ USHORT RawY,
    _Out_ PLONG OutScreenX,
    _Out_ PLONG OutScreenY
);

VOID OsuKernelProcessPacket(
    _In_ PDEVICE_CONTEXT DevCtx,
    _In_reads_bytes_(Length) PUCHAR Buffer,
    _In_ size_t Length
);

// Backward-compatibility aliases
#define Ctl472EvtDriverUnload          OsuKernelEvtDriverUnload
#define Ctl472EvtDeviceAdd             OsuKernelEvtDeviceAdd
#define Ctl472EvtDevicePrepareHardware OsuKernelEvtDevicePrepareHardware
#define Ctl472EvtDeviceReleaseHardware OsuKernelEvtDeviceReleaseHardware
#define Ctl472EvtDeviceD0Entry         OsuKernelEvtDeviceD0Entry
#define Ctl472EvtDeviceD0Exit          OsuKernelEvtDeviceD0Exit
#define Ctl472EvtUsbReadComplete       OsuKernelEvtUsbReadComplete
#define Ctl472EvtUsbReadFailed         OsuKernelEvtUsbReadFailed
#define Ctl472EvtIoDeviceControl       OsuKernelEvtIoDeviceControl
#define Ctl472ConfigureUsb             OsuKernelConfigureUsb
#define Ctl472SendFeatureReport        OsuKernelSendFeatureReport
#define Ctl472RecalculateScales        OsuKernelRecalculateScales
#define Ctl472TransformCoordinates     OsuKernelTransformCoordinates
#define Ctl472ProcessPacket            OsuKernelProcessPacket
