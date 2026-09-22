#include "osuKernel.h"

//
// I/O Device Control Handler for osuKernel
// Handles communications from Frontend GUI and user-mode tools
//
VOID OsuKernelEvtIoDeviceControl(
    _In_ WDFQUEUE   Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t     OutputBufferLength,
    _In_ size_t     InputBufferLength,
    _In_ ULONG      IoControlCode
)
{
    NTSTATUS status = STATUS_SUCCESS;
    size_t bytesReturned = 0;
    PDEVICE_CONTEXT devCtx = DeviceGetContext(WdfIoQueueGetDevice(Queue));

    switch (IoControlCode) {

    //
    // SET_AREA: GUI updates tablet input area and screen output bounds
    //
    case IOCTL_OSUKERNEL_SET_AREA: {
        if (InputBufferLength < sizeof(OSUKERNEL_AREA_CONFIG)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        POSUKERNEL_AREA_CONFIG newArea = NULL;
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(OSUKERNEL_AREA_CONFIG), (PVOID*)&newArea, NULL);
        if (!NT_SUCCESS(status) || newArea == NULL) {
            break;
        }

        // Validate area dimensions
        if (newArea->TabletInputWidth == 0 || newArea->TabletInputHeight == 0 ||
            newArea->ScreenOutputWidth == 0 || newArea->ScreenOutputHeight == 0) {
            status = STATUS_INVALID_PARAMETER;
            break;
        }

        // Atomically update active area and recalculate fixed-point scales
        WdfSpinLockAcquire(devCtx->ConfigLock);
        RtlCopyMemory(&devCtx->AreaConfig, newArea, sizeof(OSUKERNEL_AREA_CONFIG));
        OsuKernelRecalculateScales(devCtx);
        WdfSpinLockRelease(devCtx->ConfigLock);

        KdPrintDbg("IOCTL_OSUKERNEL_SET_AREA applied: TabletArea=[%ld,%ld %ux%u], Screen=[%ld,%ld %ux%u]\n",
            devCtx->AreaConfig.TabletInputX, devCtx->AreaConfig.TabletInputY,
            devCtx->AreaConfig.TabletInputWidth, devCtx->AreaConfig.TabletInputHeight,
            devCtx->AreaConfig.ScreenOutputX, devCtx->AreaConfig.ScreenOutputY,
            devCtx->AreaConfig.ScreenOutputWidth, devCtx->AreaConfig.ScreenOutputHeight);

        break;
    }

    //
    // GET_AREA: Returns current active area mapping to GUI
    //
    case IOCTL_OSUKERNEL_GET_AREA: {
        if (OutputBufferLength < sizeof(OSUKERNEL_AREA_CONFIG)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        POSUKERNEL_AREA_CONFIG outArea = NULL;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(OSUKERNEL_AREA_CONFIG), (PVOID*)&outArea, NULL);
        if (!NT_SUCCESS(status) || outArea == NULL) {
            break;
        }

        WdfSpinLockAcquire(devCtx->ConfigLock);
        RtlCopyMemory(outArea, &devCtx->AreaConfig, sizeof(OSUKERNEL_AREA_CONFIG));
        WdfSpinLockRelease(devCtx->ConfigLock);

        bytesReturned = sizeof(OSUKERNEL_AREA_CONFIG);
        break;
    }

    //
    // SET_CONFIG: Low-latency options, pressure curves, click threshold
    //
    case IOCTL_OSUKERNEL_SET_CONFIG: {
        if (InputBufferLength < sizeof(OSUKERNEL_SETTINGS)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        POSUKERNEL_SETTINGS newSettings = NULL;
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(OSUKERNEL_SETTINGS), (PVOID*)&newSettings, NULL);
        if (!NT_SUCCESS(status) || newSettings == NULL) {
            break;
        }

        WdfSpinLockAcquire(devCtx->ConfigLock);
        RtlCopyMemory(&devCtx->Settings, newSettings, sizeof(OSUKERNEL_SETTINGS));
        OsuKernelRecalculateScales(devCtx);
        WdfSpinLockRelease(devCtx->ConfigLock);

        break;
    }

    //
    // GET_CONFIG: Returns active performance settings
    //
    case IOCTL_OSUKERNEL_GET_CONFIG: {
        if (OutputBufferLength < sizeof(OSUKERNEL_SETTINGS)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        POSUKERNEL_SETTINGS outSettings = NULL;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(OSUKERNEL_SETTINGS), (PVOID*)&outSettings, NULL);
        if (!NT_SUCCESS(status) || outSettings == NULL) {
            break;
        }

        WdfSpinLockAcquire(devCtx->ConfigLock);
        RtlCopyMemory(outSettings, &devCtx->Settings, sizeof(OSUKERNEL_SETTINGS));
        WdfSpinLockRelease(devCtx->ConfigLock);

        bytesReturned = sizeof(OSUKERNEL_SETTINGS);
        break;
    }

    //
    // GET_STATS: Real-time telemetry (PPS, raw coordinates, pressure, buttons)
    //
    case IOCTL_OSUKERNEL_GET_STATS: {
        if (OutputBufferLength < sizeof(OSUKERNEL_DRIVER_STATS)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        POSUKERNEL_DRIVER_STATS outStats = NULL;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(OSUKERNEL_DRIVER_STATS), (PVOID*)&outStats, NULL);
        if (!NT_SUCCESS(status) || outStats == NULL) {
            break;
        }

        WdfSpinLockAcquire(devCtx->StatsLock);
        RtlCopyMemory(outStats, &devCtx->Stats, sizeof(OSUKERNEL_DRIVER_STATS));

        // Sub-packet high-frequency trajectory interpolation up to 8 kHz
        if (devCtx->Settings.InterpolationRate > 0 && devCtx->Stats.InProximity != 0) {
            LARGE_INTEGER nowInterp = KeQueryPerformanceCounter(NULL);
            LONGLONG deltaTicks = nowInterp.QuadPart - devCtx->InterpCurrTime.QuadPart;
            // Cap extrapolation window to 25ms to prevent overshoot when stopping
            LONGLONG maxWindow = (devCtx->PerfFreq.QuadPart > 0) ? (devCtx->PerfFreq.QuadPart / 40) : 250000;

            if (deltaTicks > 0 && deltaTicks < maxWindow && devCtx->InterpDeltaTicks > 0) {
                LONG interpX = devCtx->InterpCurrScreenX + (LONG)((deltaTicks * (LONGLONG)devCtx->InterpDeltaScreenX) / devCtx->InterpDeltaTicks);
                LONG interpY = devCtx->InterpCurrScreenY + (LONG)((deltaTicks * (LONGLONG)devCtx->InterpDeltaScreenY) / devCtx->InterpDeltaTicks);
                outStats->LastScreenX = interpX;
                outStats->LastScreenY = interpY;
                devCtx->DispatchedCounter++;
            }
        }

        WdfSpinLockRelease(devCtx->StatsLock);

        bytesReturned = sizeof(OSUKERNEL_DRIVER_STATS);
        break;
    }

    //
    // RESET_STATS: Resets packet counters
    //
    case IOCTL_OSUKERNEL_RESET_STATS: {
        WdfSpinLockAcquire(devCtx->StatsLock);
        devCtx->Stats.TotalPackets = 0;
        devCtx->Stats.DroppedPackets = 0;
        devCtx->PpsCounter = 0;
        devCtx->Stats.PacketsPerSecond = 0;
        WdfSpinLockRelease(devCtx->StatsLock);
        break;
    }

    default:
        status = STATUS_INVALID_DEVICE_REQUEST;
        break;
    }

    WdfRequestCompleteWithInformation(Request, status, bytesReturned);
}
